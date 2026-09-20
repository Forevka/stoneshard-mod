// Headless control for the debugger.
//
// The overlay's buttons are ImGui widgets drawn inside the game, so nothing
// outside the process can reach them - and forcing the game window to the
// foreground to click it is both unreliable and rude. A one-line command file
// solves it: anything that can write a file can drive a trace, arm a
// breakpoint, or dump memory, and every result lands in the log.
//
// The file is polled on the game thread (from the Present hook), which is also
// the only thread where calling into GML is safe.

#include "remote.h"
#include "builtins.h"
#include "items.h"
#include "console.h"
#include "enemies.h"
#include "gamespeed.h"
#include "gml.h"
#include "log.h"
#include "paths.h"
#include "savebackup.h"
#include "symbols.h"
#include "tracer.h"

#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace mod::remote {
namespace {

std::string CommandPath() { return paths::File("debug-cmd.txt"); }
std::string ReplyPath()   { return paths::File("debug-reply.txt"); }

unsigned g_frame = 0;

std::vector<std::string> Split(const std::string& s) {
    std::vector<std::string> out;
    std::string cur;
    bool quoted = false;
    for (char c : s) {
        if (c == '"') { quoted = !quoted; continue; }
        if (!quoted && (c == ' ' || c == '\t' || c == '\r' || c == '\n')) {
            if (!cur.empty()) { out.push_back(cur); cur.clear(); }
            continue;
        }
        cur += c;
    }
    if (!cur.empty()) out.push_back(cur);
    return out;
}

void Reply(const char* fmt, ...) {
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);

    Logf("remote: %s", buf);
    if (std::FILE* f = std::fopen(ReplyPath().c_str(), "a")) {
        SYSTEMTIME st{};
        GetLocalTime(&st);
        std::fprintf(f, "%02u:%02u:%02u  %s\n", st.wHour, st.wMinute, st.wSecond, buf);
        std::fclose(f);
    }
}

// Dumps instance memory the same way the Inspector tab does, but to the reply
// file so it can be read from outside the game.
void DumpMemory(const void* base, int bytes) {
    if (!base) { Reply("dump: no instance"); return; }
    std::vector<unsigned char> buf(static_cast<std::size_t>(bytes));
    if (!gml::ReadMemory(base, buf.data(), bytes)) { Reply("dump: unreadable at %p", base); return; }

    Reply("dump of %p (%d bytes)", base, bytes);
    for (int off = 0; off + 8 <= bytes; off += 8) {
        std::uint64_t raw;
        double        d;
        std::memcpy(&raw, buf.data() + off, 8);
        std::memcpy(&d,   buf.data() + off, 8);

        const auto p = static_cast<std::uintptr_t>(raw);
        const char* hint = sym::TextRange().contains(p)  ? "-> .text"
                         : sym::RdataRange().contains(p) ? "-> .rdata"
                         : sym::DataRange().contains(p)  ? "-> .data" : "";
        const bool sane = (d == d) && ((d == 0.0) || (d > 1e-6 && d < 1e9) || (d < -1e-6 && d > -1e9));
        if (hint[0])      Reply("   +0x%03X  %016llX  %s", off, (unsigned long long)raw, hint);
        else if (sane)    Reply("   +0x%03X  %016llX  %.3f", off, (unsigned long long)raw, d);
        else              Reply("   +0x%03X  %016llX", off, (unsigned long long)raw);
    }
}

void Execute(const std::string& line) {
    const auto tok = Split(line);
    if (tok.empty()) return;
    Reply("command: %s", line.c_str());

    if (tok[0] == "status") {
        double x = 0, y = 0;
        const bool pos = gml::PlayerPosition(x, y);
        Reply("symbols=%zu healthy=%d | gml=%d abi=%d | tracer=%d (%s)",
              sym::Count(), sym::Healthy() ? 1 : 0,
              gml::Ready() ? 1 : 0, gml::AbiProven() ? 1 : 0,
              tracer::Ready() ? 1 : 0, tracer::Status());
        Reply("player=%p pos=%s (%.2f, %.2f) | recording=%d",
              gml::PlayerInstance(), pos ? "known" : "unknown", x, y,
              tracer::Recording() ? 1 : 0);
        Reply("builtins=%zu ready=%d (%s) | selftest: %s",
              builtins::Count(), builtins::Ready() ? 1 : 0, builtins::Status(),
              builtins::SelfTestReport());
        Reply("datadir=%s%s pid=%lu",
              paths::DataDir().c_str(),
              paths::Overridden() ? " (SSMOD_DATA_DIR)" : " (compiled default)",
              GetCurrentProcessId());
        return;
    }

    if (tok[0] == "trace") {
        tracer::Options o;
        if (tok.size() > 1) o.durationMs = std::atoi(tok[1].c_str());
        if (tok.size() > 2) std::snprintf(o.filter, sizeof(o.filter), "%s", tok[2].c_str());
        if (o.durationMs < 200) o.durationMs = 200;
        Reply(tracer::StartRecording(o) ? "trace started (%d ms, filter=\"%s\")"
                                        : "trace FAILED to start (%d ms, filter=\"%s\")",
              o.durationMs, o.filter);
        return;
    }

    if (tok[0] == "bp") {
        if (tok.size() < 2) { Reply("usage: bp <symbol>"); return; }
        Reply(tracer::SetBreakpoint(tok[1], false, 20) ? "breakpoint armed on %s"
                                                       : "breakpoint FAILED on %s",
              tok[1].c_str());
        return;
    }
    if (tok[0] == "bpoff") { tracer::ClearBreakpoint(); Reply("breakpoint cleared"); return; }

    if (tok[0] == "dump") {
        const void* base = gml::PlayerInstance();
        int bytes = 0x100;
        if (tok.size() > 1 && tok[1] != "player")
            base = reinterpret_cast<const void*>(std::strtoull(tok[1].c_str(), nullptr, 16));
        if (tok.size() > 2) bytes = std::atoi(tok[2].c_str());
        if (bytes < 16)   bytes = 16;
        if (bytes > 4096) bytes = 4096;
        DumpMemory(base, bytes);
        return;
    }

    if (tok[0] == "call") {
        if (tok.size() < 2) { Reply("usage: call <symbol> [args...]"); return; }
        console::Execute(line.substr(line.find("call") + 5));
        Reply("call dispatched (see the Console tab for the result)");
        return;
    }

    // `call` reaches SCRIPTS, which are the only thing sym::Find knows about.
    // Builtins live in a separate runtime table entirely, so they need their
    // own command and their own invoker - the ABIs differ (§2.4).
    if (tok[0] == "callb") {
        if (tok.size() < 2) { Reply("usage: callb <builtin> [args...]"); return; }

        const builtins::Builtin b = builtins::Find(tok[1]);
        if (!b.fn) {
            Reply("callb: '%s' is not a registered builtin (%zu known, %s)",
                  tok[1].c_str(), builtins::Count(), builtins::Status());
            return;
        }
        backup::EnsureBackupOnce();

        const std::size_t argc = tok.size() - 2;
        std::vector<std::string> storage(argc);           // must outlive the call
        std::vector<gml::RValue> args(argc ? argc : 1);

        for (std::size_t i = 0; i < argc; ++i) {
            const std::string& t = tok[i + 2];
            char* end = nullptr;
            const double num = std::strtod(t.c_str(), &end);
            if (t == "undefined" || t == "_") {
                gml::SetUndefined(args[i]);
            } else if (!t.empty() && end && *end == '\0') {
                gml::SetReal(args[i], num);
            } else {
                storage[i] = t;
                if (!gml::SetString(args[i], storage[i].c_str())) {
                    Reply("callb: could not build string argument %zu", i);
                    return;
                }
            }
        }

        void* self = gml::PlayerInstance() ? gml::PlayerInstance() : gml::CurrentSelf();
        gml::RValue r{};
        if (!builtins::Call(tok[1], &r, argc ? args.data() : nullptr,
                            static_cast<int>(argc), self)) {
            Reply("callb: %s FAILED (gave %d args, registry says argc=%d)",
                  tok[1].c_str(), static_cast<int>(argc), b.argc);
            return;
        }
        Reply("callb: %s -> kind=%d  %s", tok[1].c_str(), r.kind, gml::ToString(r).c_str());
        return;
    }

    if (tok[0] == "getvar") {
        if (tok.size() < 2) { Reply("usage: getvar <name>"); return; }
        const builtins::Handle h = builtins::PlayerHandle();
        gml::RValue v{};
        if (builtins::GetVar(h, tok[1].c_str(), &v))
            Reply("getvar %s -> kind=%d  %s   (via %s, self=%p)",
                  tok[1].c_str(), v.kind, gml::ToString(v).c_str(),
                  h.haveRef ? "instance ref" : "-1/self", h.self);
        else
            Reply("getvar %s -> FAILED   (via %s, self=%p)", tok[1].c_str(),
                  h.haveRef ? "instance ref" : "-1/self", h.self);
        return;
    }

    if (tok[0] == "setvar") {
        if (tok.size() < 3) { Reply("usage: setvar <name> <value>"); return; }
        backup::EnsureBackupOnce();

        std::string storage;                              // must outlive the call
        gml::RValue v{};
        char* end = nullptr;
        const double num = std::strtod(tok[2].c_str(), &end);
        if (end && *end == '\0') {
            gml::SetReal(v, num);
        } else {
            storage = tok[2];
            if (!gml::SetString(v, storage.c_str())) { Reply("setvar: bad value"); return; }
        }

        const builtins::Handle h = builtins::PlayerHandle();
        Reply(builtins::SetVar(h, tok[1].c_str(), v) ? "setvar %s = %s -> ok"
                                                     : "setvar %s = %s -> FAILED",
              tok[1].c_str(), tok[2].c_str());
        return;
    }

    // The gear-stat experiment: which instance carries a weapon's rolled
    // stats, and under what names. Everything an item constructor would do
    // depends on the answer, so it is measured rather than assumed.
    if (tok[0] == "itemprobe") {
        if (tok.size() < 2) { Reply("usage: itemprobe <gear display name>"); return; }

        // The name is a CSV display name and contains spaces ("Militia
        // Falchion"), so rejoin everything the tokeniser split apart.
        std::string name = tok[1];
        for (std::size_t i = 2; i < tok.size(); ++i) { name += ' '; name += tok[i]; }

        for (const std::string& l : items::Probe(name, 0)) Reply("%s", l.c_str());
        return;
    }

    // What the GAME passes to scr_weapon_loot, recorded from a real call. Our
    // own spawn fills only the first five, which is the leading theory for why
    // everything it makes comes out Common and uncursed.
    // Record what the GAME passes to a script, instead of guessing.
    //
    // scr_skill_open and scr_buff_param both FAULTED when called with a name
    // string - the argument is something else entirely. Guessing further risks
    // the player's session for no reason, and this is the technique that
    // settled scr_weapon_loot: detour it, let the game make a real call, and
    // read the exact self/other and argument values back.
    // Movement animation is the main wall-clock cost in a turn-based game, so
    // this is worth having outside the overlay too.
    if (tok[0] == "speed") {
        if (tok.size() < 2) {
            Reply("speed: %.0f steps/sec (baseline %.0f, hold=%d)",
                  gamespeed::Current(), gamespeed::Baseline(), gamespeed::Hold() ? 1 : 0);
            return;
        }
        if (tok[1] == "reset") {
            const double base = gamespeed::Baseline();
            Reply(base > 0.0 && gamespeed::Set(base) ? "speed: reset to %.0f"
                                                     : "speed: reset FAILED (%.0f)", base);
            return;
        }

        // A bare number is a MULTIPLIER of the game's own baseline, not raw
        // steps/sec - "speed 4" should mean four times faster regardless of
        // what the game happens to run at.
        const double mult = std::atof(tok[1].c_str());
        const double base = gamespeed::Baseline() > 0.0 ? gamespeed::Baseline()
                                                        : gamespeed::Current();
        if (mult <= 0.0 || base <= 0.0) { Reply("usage: speed <multiplier>|reset"); return; }

        Reply(gamespeed::Set(base * mult) ? "speed: %.2fx -> %.0f steps/sec"
                                          : "speed: FAILED at %.2fx (%.0f)",
              mult, base * mult);
        return;
    }

    if (tok[0] == "capture") {
        if (tok.size() > 1) {
            Reply(gml::InstallCapture(tok[1]) ? "capture: armed on %s - now do the thing in-game"
                                              : "capture: could NOT arm on %s",
                  tok[1].c_str());
            return;
        }

        const auto& c = gml::LastCapture();
        if (!c.valid) { Reply("capture: nothing recorded yet"); return; }
        Reply("capture: %s argc=%d hits=%u self=%p other=%p caller=%s",
              c.symbol.c_str(), c.argc, c.hits, c.self, c.other, c.caller.c_str());
        for (std::size_t i = 0; i < c.args.size(); ++i)
            Reply("   arg[%zu] %s", i, c.args[i].c_str());
        return;
    }

    if (tok[0] == "weaponrec") {
        const auto& rec = gml::WeaponRecord();
        if (!rec.valid) { Reply("weaponrec: nothing captured yet"); return; }
        Reply("weaponrec: %s argc=%d hits=%u self=%p caller=%s",
              rec.symbol.c_str(), rec.argc, rec.hits, rec.self, rec.caller.c_str());
        for (std::size_t i = 0; i < rec.args.size(); ++i)
            Reply("   arg[%zu] %s", i, rec.args[i].c_str());
        return;
    }

    // spawnrare "<gear name>" <rarity 1-7>
    // Rarity is argument 4 of scr_weapon_loot. Asking for 3 or 6 makes the
    // GAME roll the bonus stats, which is a truer "enchanted item" than
    // writing stat keys in by hand.
    if (tok[0] == "spawnrare") {
        if (tok.size() < 3) {
            Reply("usage: spawnrare \"<gear name>\" <rarity 1-7>  (1 Common ... 6 Unique)");
            return;
        }
        const int rarity = std::atoi(tok[2].c_str());
        if (rarity < 1 || rarity > 7) { Reply("spawnrare: rarity must be 1-7"); return; }

        Reply(items::SpawnGear(tok[1], 48.0, 0.0, rarity, nullptr)
                  ? "spawnrare: %s as %s (%d)" : "spawnrare: FAILED %s as %s (%d)",
              tok[1].c_str(), items::RarityName(rarity), rarity);
        return;
    }

    // Straight into the inventory - no drop, no walking over it.
    if (tok[0] == "giveweapon") {
        if (tok.size() < 2) { Reply("usage: giveweapon \"<gear name>\" [rarity 1-7]"); return; }
        const int rarity = tok.size() > 2 ? std::atoi(tok[2].c_str()) : 1;

        Reply(items::AddWeaponToInventory(tok[1], rarity)
                  ? "giveweapon: %s as %s" : "giveweapon: FAILED %s as %s",
              tok[1].c_str(), items::RarityName(rarity));
        return;
    }

    // The overlay's Enemies tab, minus the overlay: list what is in the room,
    // dump one row's variables, or destroy one by index.
    if (tok[0] == "enemies") {
        enemies::Refresh();
        const auto& rows = enemies::Roster();

        if (tok.size() == 1) {
            Reply("enemies: %zu listed, the game reports %d (tracker %s)",
                  rows.size(), enemies::Reported(),
                  enemies::Tracking() ? "on" : "off");
            for (std::size_t i = 0; i < rows.size(); ++i) {
                const auto& e = rows[i];
                Reply("   [%zu] %-24s hp=%.0f/%.0f lvl=%.0f dist=%.0f %s",
                      i, e.name.c_str(), e.hp, e.maxHp, e.level, e.dist,
                      e.tracked ? "tracked" : "listed");
            }
            if (rows.empty() && enemies::LastError()[0]) Reply("   %s", enemies::LastError());
            return;
        }

        if (tok[1] == "vars" && tok.size() > 2) {
            const int n = std::atoi(tok[2].c_str());
            if (n < 0 || n >= static_cast<int>(rows.size())) { Reply("enemies: no row %d", n); return; }
            for (const std::string& l : enemies::Probe(rows[static_cast<std::size_t>(n)], 0))
                Reply("%s", l.c_str());
            return;
        }

        // Destroying a row has to be spelled out. A bare index used to kill it,
        // which made a typo destructive; there is no kill path any more and the
        // remaining one says what it does.
        if (tok[1] != "remove" || tok.size() < 3) {
            Reply("usage: enemies [remove <n>|vars <n>]");
            return;
        }

        const int n = std::atoi(tok[2].c_str());
        if (n < 0 || n >= static_cast<int>(rows.size())) { Reply("enemies: no row %d", n); return; }

        const auto& e = rows[static_cast<std::size_t>(n)];
        Reply("enemies: remove [%d] %s -> %s", n, e.name.c_str(),
              enemies::Remove(e) ? "ok" : enemies::LastError());
        return;
    }

    if (tok[0] == "itemscan") {
        const int limit = tok.size() > 1 ? std::atoi(tok[1].c_str()) : 0;
        for (const std::string& l : items::ScanInventory(limit)) Reply("%s", l.c_str());
        return;
    }

    if (tok[0] == "names") {
        int limit = tok.size() > 1 ? std::atoi(tok[1].c_str()) : 40;
        if (limit <= 0) limit = 40;

        const builtins::Handle h = builtins::PlayerHandle();
        std::vector<std::string> out;
        const int total = builtins::VarNames(h, out, limit);
        if (total < 0) { Reply("names: FAILED (via %s, self=%p)",
                                h.haveRef ? "instance ref" : "-1/self", h.self); return; }

        Reply("names: o_player has %d instance variables, listing %zu", total, out.size());
        for (const std::string& n : out) Reply("   %s", n.c_str());
        return;
    }

    // vars <hex instance pointer> [limit]
    //
    // `names` and `getvar` both go through PlayerHandle, so neither can look at
    // anything but the player. A breakpoint hands back a raw CInstance* for
    // whatever it caught - a freshly created buff, say - and the only way to
    // learn how that object is parameterised is to read its variables by name.
    // builtins::SelfHandle turns the pointer into a handle the reflection
    // builtins accept, which is the same route the Enemies tab already uses.
    if (tok[0] == "vars") {
        if (tok.size() < 2) { Reply("usage: vars <hex instance ptr> [limit]"); return; }

        void* inst = reinterpret_cast<void*>(
            std::strtoull(tok[1].c_str(), nullptr, 16));
        if (!inst) { Reply("vars: %s is not a usable pointer", tok[1].c_str()); return; }

        int limit = tok.size() > 2 ? std::atoi(tok[2].c_str()) : 80;
        if (limit <= 0) limit = 80;

        int total = 0;
        const auto lines = items::DumpVars(builtins::SelfHandle(inst), limit, &total);
        if (lines.empty()) {
            Reply("vars: nothing readable at %p - the instance may already be gone", inst);
            return;
        }
        Reply("vars: %p has %d instance variables, listing %zu", inst, total, lines.size());
        for (const std::string& l : lines) Reply("%s", l.c_str());
        return;
    }

    Reply("unknown command: %s", tok[0].c_str());
}

} // namespace

void Poll() {
    // Once a second is plenty and keeps the per-frame cost at nothing.
    if (++g_frame % 60) return;

    std::FILE* f = std::fopen(CommandPath().c_str(), "r");
    if (!f) return;

    char line[512] = {};
    const bool got = std::fgets(line, sizeof(line), f) != nullptr;
    std::fclose(f);
    std::remove(CommandPath().c_str());          // consume it, so it runs once

    if (!got) return;

    // fgets keeps the newline, and the `call` path forwards the tail of this
    // string verbatim to the console. Trim before anyone sees it.
    std::string cmd(line);
    while (!cmd.empty() && (cmd.back() == '\n' || cmd.back() == '\r' ||
                            cmd.back() == ' '  || cmd.back() == '\t'))
        cmd.pop_back();

    if (!cmd.empty()) Execute(cmd);
}

} // namespace mod::remote
