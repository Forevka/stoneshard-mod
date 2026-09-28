// Runtime resolver for GameMaker YYC script registrations.
//
// YYC emits rows of {const char* name, void* func, void* slot} into .data, where
// the name points at a "gml_..." string in .rdata and func points into .text.
// We scan for that shape rather than assuming a stride, because the rows are
// fragmented (a contiguous walk from ConsoleCommand runs out after 175 entries).
//
// Everything is resolved by NAME against the live image, so a game update that
// relocates code changes nothing here.

#include "symbols.h"
#include "log.h"

#include <windows.h>
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstring>
#include <unordered_map>

namespace mod::sym {
namespace {

using Range = SectionRange;

Range    g_text, g_rdata, g_data;
DWORD    g_imageSize = 0;

std::vector<Entry>                          g_entries;
std::unordered_map<std::string, void*>      g_index;
std::vector<std::string>                    g_consoleCommands;
// Same entries ordered by address, so a code pointer can be mapped to a name.
std::vector<Entry>                          g_byAddress;

bool        g_healthy = false;
std::string g_health  = "not scanned";

// Minimum plausible symbol count for ANY YYC game. Stoneshard resolves 34,167
// and Dwarf Eats Mountain 4,968, so the floor only has to separate "the table
// shape changed and nothing matched" from a real, if small, game.
constexpr std::size_t kMinEntries = 50;

bool SectionRanges(HMODULE mod) {
    auto base = reinterpret_cast<std::uintptr_t>(mod);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;

    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return false;

    g_imageSize = nt->OptionalHeader.SizeOfImage;

    auto* sec = IMAGE_FIRST_SECTION(nt);
    for (unsigned i = 0; i < nt->FileHeader.NumberOfSections; ++i) {
        char name[9]{};
        std::memcpy(name, sec[i].Name, 8);

        Range r;
        r.lo = base + sec[i].VirtualAddress;
        r.hi = r.lo + sec[i].Misc.VirtualSize;

        if (!std::strcmp(name, ".text"))  g_text  = r;
        if (!std::strcmp(name, ".rdata")) g_rdata = r;
        if (!std::strcmp(name, ".data"))  g_data  = r;
    }
    return g_text.hi && g_rdata.hi && g_data.hi;
}

// Bounded read of a NUL-terminated string that must live entirely inside .rdata.
const char* GmlStringAt(std::uintptr_t p) {
    if (!g_rdata.contains(p)) return nullptr;

    const auto* s = reinterpret_cast<const char*>(p);
    if (s[0] != 'g' || s[1] != 'm' || s[2] != 'l' || s[3] != '_') return nullptr;

    const std::uintptr_t limit = std::min<std::uintptr_t>(p + 220, g_rdata.hi);
    for (std::uintptr_t q = p; q < limit; ++q) {
        const auto c = static_cast<unsigned char>(*reinterpret_cast<const char*>(q));
        if (c == 0) return (q > p + 4) ? s : nullptr;
        if (c < 0x20 || c > 0x7e) return nullptr;
    }
    return nullptr;
}

} // namespace

bool Scan() {
    g_entries.clear();
    g_byAddress.clear();
    g_index.clear();
    g_consoleCommands.clear();
    g_healthy = false;

    HMODULE game = GetModuleHandleW(nullptr);
    if (!SectionRanges(game)) {
        g_health = "could not read PE section ranges";
        Logf("[!] symbols: %s", g_health.c_str());
        return false;
    }

    // Fingerprint: makes a broken game update obvious at a glance in the log.
    Logf("symbols: image=%p size=%lu  .text=%zu KB  .rdata=%zu KB  .data=%zu KB",
         game, g_imageSize,
         (g_text.hi - g_text.lo) / 1024,
         (g_rdata.hi - g_rdata.lo) / 1024,
         (g_data.hi - g_data.lo) / 1024);

    const DWORD started = GetTickCount();
    g_entries.reserve(40000);

    // Walk .data at pointer alignment looking for {name -> .rdata "gml_*", func -> .text}.
    const auto* p   = reinterpret_cast<const std::uintptr_t*>(g_data.lo);
    const auto* end = reinterpret_cast<const std::uintptr_t*>(g_data.hi - 16);

    for (; p < end; ++p) {
        const char* name = GmlStringAt(p[0]);
        if (!name) continue;
        if (!g_text.contains(p[1])) continue;

        Entry e{name, reinterpret_cast<void*>(p[1])};
        if (g_index.emplace(name, e.func).second)
            g_entries.push_back(e);
    }

    const DWORD elapsed = GetTickCount() - started;

    // Harvest the built-in console command names for autocomplete.
    constexpr char kPrefix[] = "gml_Script_scr_console_";
    constexpr std::size_t kPrefixLen = sizeof(kPrefix) - 1;
    for (const Entry& e : g_entries) {
        if (std::strncmp(e.name, kPrefix, kPrefixLen) != 0) continue;
        std::string shortName(e.name + kPrefixLen);
        if (shortName.empty()) continue;
        // Skip the _help partners and compiler-generated inner functions.
        if (shortName.size() > 5 &&
            shortName.compare(shortName.size() - 5, 5, "_help") == 0) continue;
        if (shortName.find("_gml_") != std::string::npos) continue;
        g_consoleCommands.push_back(std::move(shortName));
    }
    std::sort(g_consoleCommands.begin(), g_consoleCommands.end());
    g_consoleCommands.erase(
        std::unique(g_consoleCommands.begin(), g_consoleCommands.end()),
        g_consoleCommands.end());

    g_byAddress = g_entries;
    std::sort(g_byAddress.begin(), g_byAddress.end(),
              [](const Entry& a, const Entry& b) { return a.func < b.func; });

    std::sort(g_entries.begin(), g_entries.end(),
              [](const Entry& a, const Entry& b) { return std::strcmp(a.name, b.name) < 0; });

    Logf("symbols: %zu resolved in %lu ms (%zu console commands)",
         g_entries.size(), elapsed, g_consoleCommands.size());

    // ---- health check -------------------------------------------------------
    if (g_entries.size() < kMinEntries) {
        char buf[160];
        std::snprintf(buf, sizeof(buf),
                      "only %zu symbols resolved (expected >= %zu) - table shape changed?",
                      g_entries.size(), kMinEntries);
        g_health  = buf;
        Logf("[!] symbols: %s", g_health.c_str());
        return false;
    }

    g_healthy = true;
    g_health  = "ok";
    Logf("symbols: health check passed");
    return true;
}

bool        Healthy()       { return g_healthy; }
const char* HealthMessage() { return g_health.c_str(); }
std::size_t Count()         { return g_entries.size(); }

void* Find(const std::string& name) {
    auto it = g_index.find(name);
    return it == g_index.end() ? nullptr : it->second;
}

void* FindScript(const std::string& shortName) {
    return Find("gml_Script_" + shortName);
}

std::vector<const Entry*> Search(const std::string& needle, std::size_t limit) {
    std::string lower = needle;
    std::transform(lower.begin(), lower.end(), lower.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });

    std::vector<const Entry*> out;
    for (const Entry& e : g_entries) {
        if (out.size() >= limit) break;
        if (lower.empty()) { out.push_back(&e); continue; }

        std::string hay = e.name;
        std::transform(hay.begin(), hay.end(), hay.begin(),
                       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        if (hay.find(lower) != std::string::npos)
            out.push_back(&e);
    }
    return out;
}

const std::vector<std::string>& ConsoleCommands() { return g_consoleCommands; }

SectionRange TextRange()  { return g_text; }
SectionRange RdataRange() { return g_rdata; }
SectionRange DataRange()  { return g_data; }

const std::vector<Entry>& All() { return g_entries; }

const char* OwnerOf(const void* addr) {
    if (g_byAddress.empty() || !addr) return nullptr;
    // Last entry whose start is <= addr.
    auto it = std::upper_bound(g_byAddress.begin(), g_byAddress.end(), addr,
                               [](const void* a, const Entry& e) { return a < e.func; });
    if (it == g_byAddress.begin()) return nullptr;
    --it;
    // Guard against attributing an address that lies far past the last function.
    const auto delta = reinterpret_cast<std::uintptr_t>(addr) -
                       reinterpret_cast<std::uintptr_t>(it->func);
    return delta < (1u << 20) ? it->name : nullptr;
}

} // namespace mod::sym
