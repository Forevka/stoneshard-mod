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
#include "console.h"
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
