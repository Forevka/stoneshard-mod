// Item catalogue, assembled from the game's own data at startup.
//
// Two sources, because Stoneshard stores items two different ways:
//
//   1. data.win OBJT chunk -> the o_inv_* objects. Categories come from the
//      GameMaker parent hierarchy (o_inv_acorn's parent is o_inv_food_parent),
//      which is the game's own grouping.
//
//   2. The exe's .rdata -> rows of the embedded weapons/armor CSVs. Gear is
//      data-driven, so there are no weapon or armor objects at all; the rows
//      look like
//          Militia Falchion;2;sword21;sword;cleaver;;Common;metal;450;...
//      i.e. field 0 name, field 2 id, field 3 category.
//      Enemy rows share the format but leave field 6 empty, which is what
//      separates them - no name lists involved.
//
// Nothing is baked in: both sources are re-read every launch.

#include "assets.h"
#include "log.h"
#include "symbols.h"

#include <windows.h>
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <cstring>
#include <unordered_map>

namespace mod::assets {
namespace {

std::vector<Item>        g_items;
std::vector<std::string> g_categories;
bool                     g_loaded = false;
std::string              g_status = "not loaded";

std::string Capitalise(std::string s) {
    if (!s.empty()) s[0] = static_cast<char>(std::toupper(static_cast<unsigned char>(s[0])));
    return s;
}

// "o_inv_food_parent" -> "Food",  "o_inv_consum_passive" -> "Consum passive"
std::string CategoryFromParent(std::string name) {
    if (name.rfind("o_inv_", 0) == 0) name.erase(0, 6);
    const std::string suffix = "_parent";
    if (name.size() > suffix.size() &&
        name.compare(name.size() - suffix.size(), suffix.size(), suffix) == 0)
        name.erase(name.size() - suffix.size());
    for (char& c : name) if (c == '_') c = ' ';
    return name.empty() ? "Misc" : Capitalise(name);
}

std::string PrettyName(std::string s) {
    if (s.rfind("o_inv_", 0) == 0) s.erase(0, 6);
    for (char& c : s) if (c == '_') c = ' ';
    return Capitalise(s);
}

// ------------------------------------------------------------- data.win side

class Mapping {
public:
    bool Open(const std::wstring& path) {
        file_ = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file_ == INVALID_HANDLE_VALUE) return false;
        LARGE_INTEGER sz{};
        if (!GetFileSizeEx(file_, &sz) || sz.QuadPart < 64) return false;
        size_ = static_cast<std::size_t>(sz.QuadPart);
        map_ = CreateFileMappingW(file_, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (!map_) return false;
        view_ = static_cast<const unsigned char*>(MapViewOfFile(map_, FILE_MAP_READ, 0, 0, 0));
        return view_ != nullptr;
    }
    ~Mapping() {
        if (view_) UnmapViewOfFile(view_);
        if (map_)  CloseHandle(map_);
        if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_);
    }
    const unsigned char* data() const { return view_; }
    std::size_t          size() const { return size_; }

    bool u32(std::size_t off, std::uint32_t& out) const {
        if (off + 4 > size_) return false;
        std::memcpy(&out, view_ + off, 4);
        return true;
    }
    bool i32(std::size_t off, std::int32_t& out) const {
        if (off + 4 > size_) return false;
        std::memcpy(&out, view_ + off, 4);
        return true;
    }
    bool str(std::size_t off, std::string& out) const {
        if (off >= size_) return false;
        const std::size_t limit = (off + 256 < size_) ? off + 256 : size_;
        for (std::size_t i = off; i < limit; ++i) {
            if (view_[i] == 0) { out.assign(reinterpret_cast<const char*>(view_ + off), i - off); return !out.empty(); }
            if (view_[i] < 0x20 || view_[i] > 0x7e) return false;
        }
        return false;
    }
private:
    HANDLE               file_ = INVALID_HANDLE_VALUE;
    HANDLE               map_  = nullptr;
    const unsigned char* view_ = nullptr;
    std::size_t          size_ = 0;
};

std::wstring DataWinPath() {
    wchar_t buf[MAX_PATH]{};
    if (!GetModuleFileNameW(GetModuleHandleW(nullptr), buf, MAX_PATH)) return {};
    std::wstring p(buf);
    const std::size_t slash = p.find_last_of(L"\\/");
    if (slash == std::wstring::npos) return {};
    return p.substr(0, slash + 1) + L"data.win";
}

// Object record layout, verified against this build: field 0 is the name string
// offset and field 7 (byte offset 28) is the parent object index, -1 for none.
constexpr std::size_t kParentFieldOffset = 28;

std::size_t LoadObjects() {
    Mapping m;
    if (!m.Open(DataWinPath())) { Logf("[!] assets: could not map data.win"); return 0; }
    if (std::memcmp(m.data(), "FORM", 4) != 0) { Logf("[!] assets: data.win not a FORM archive"); return 0; }

    std::uint32_t formSize = 0;
    m.u32(4, formSize);

    std::size_t objt = 0;
    std::size_t pos = 8;
    const std::size_t end = (8 + formSize < m.size()) ? 8 + formSize : m.size();
    while (pos + 8 <= end) {
        std::uint32_t csize = 0;
        if (!m.u32(pos + 4, csize)) break;
        if (std::memcmp(m.data() + pos, "OBJT", 4) == 0) { objt = pos + 8; break; }
        pos += 8 + csize;
    }
    if (!objt) { Logf("[!] assets: no OBJT chunk"); return 0; }

    std::uint32_t count = 0;
    if (!m.u32(objt, count) || count == 0 || count > 200000) {
        Logf("[!] assets: implausible object count");
        return 0;
    }

    std::vector<std::string>  names(count);
    std::vector<std::int32_t> parents(count, -1);
    for (std::uint32_t i = 0; i < count; ++i) {
        std::uint32_t rec = 0;
        if (!m.u32(objt + 4 + i * 4, rec) || rec == 0 || rec >= m.size()) continue;
        std::uint32_t nameOff = 0;
        if (!m.u32(rec, nameOff)) continue;
        m.str(nameOff, names[i]);
        m.i32(rec + kParentFieldOffset, parents[i]);
    }

    std::size_t added = 0;
    for (std::uint32_t i = 0; i < count; ++i) {
        if (names[i].rfind("o_inv_", 0) != 0) continue;
        // Parents themselves are abstract templates, not spawnable items.
        if (names[i].size() > 7 &&
            names[i].compare(names[i].size() - 7, 7, "_parent") == 0) continue;

        const std::int32_t p = parents[i];
        const std::string category =
            (p >= 0 && static_cast<std::uint32_t>(p) < count && !names[p].empty())
                ? CategoryFromParent(names[p]) : "Misc";

        g_items.push_back({names[i], PrettyName(names[i]), category,
                           static_cast<int>(i), Source::Object});
        ++added;
    }
    return added;
}

// ----------------------------------------------------------------- CSV side

bool SplitRow(const char* s, std::size_t len, std::vector<std::string>& out) {
    out.clear();
    std::string cur;
    for (std::size_t i = 0; i < len; ++i) {
        if (s[i] == ';') { out.push_back(cur); cur.clear(); }
        else cur += s[i];
    }
    out.push_back(cur);
    return out.size() >= 8;
}

std::size_t LoadCsvRows() {
    const auto rdata = sym::RdataRange();
    if (!rdata.hi) return 0;

    std::size_t added = 0;
    std::vector<std::string> f;

    const auto* p   = reinterpret_cast<const char*>(rdata.lo);
    const auto* end = reinterpret_cast<const char*>(rdata.hi);

    // Walk NUL-terminated printable runs.
    const char* cur = p;
    while (cur < end) {
        if (*cur == '\0') { ++cur; continue; }

        const char* start = cur;
        std::size_t semis = 0;
        while (cur < end && *cur != '\0') {
            const auto c = static_cast<unsigned char>(*cur);
            if (c < 0x20 || c > 0x7e) break;
            if (c == ';') ++semis;
            ++cur;
            if (static_cast<std::size_t>(cur - start) > 900) break;
        }
        const std::size_t len = static_cast<std::size_t>(cur - start);
        while (cur < end && *cur != '\0') ++cur;   // skip any tail

        if (len < 30 || semis < 20) continue;
        if (start[0] == '/') continue;             // "// CLEAVERS;;;;" comment rows
        if (!SplitRow(start, len, f)) continue;

        const std::string& name = f[0];
        const std::string& id   = f[2];
        const std::string& cat  = f[3];

        // Enemy rows use the same shape but leave the rarity/material column
        // empty; that is the whole discriminator.
        if (f[6].empty()) continue;
        if (name.empty() || !std::isupper(static_cast<unsigned char>(name[0]))) continue;
        if (id.empty() || cat.empty()) continue;

        auto ok = [](const std::string& s) {
            return std::all_of(s.begin(), s.end(), [](unsigned char c) {
                return std::isalnum(c) || c == '_';
            });
        };
        if (!ok(id) || !ok(cat)) continue;
        // Drops the numeric balance-table rows.
        if (std::none_of(cat.begin(), cat.end(),
                         [](unsigned char c) { return std::isalpha(c) != 0; })) continue;

        g_items.push_back({id, name, Capitalise(cat), -1, Source::Csv});
        ++added;
    }
    return added;
}

} // namespace

bool Load() {
    g_items.clear();
    g_categories.clear();
    g_loaded = false;

    const DWORD t0 = GetTickCount();
    const std::size_t objects = LoadObjects();
    const std::size_t csv     = LoadCsvRows();
    const DWORD elapsed = GetTickCount() - t0;

    // De-duplicate ids (a few rows repeat across tables).
    std::sort(g_items.begin(), g_items.end(), [](const Item& a, const Item& b) {
        if (a.category != b.category) return a.category < b.category;
        return a.display < b.display;
    });

    for (const Item& i : g_items) g_categories.push_back(i.category);
    std::sort(g_categories.begin(), g_categories.end());
    g_categories.erase(std::unique(g_categories.begin(), g_categories.end()),
                       g_categories.end());

    g_loaded = !g_items.empty();

    char buf[192];
    std::snprintf(buf, sizeof(buf),
                  "%zu items (%zu objects + %zu gear rows) in %zu categories",
                  g_items.size(), objects, csv, g_categories.size());
    g_status = buf;
    Logf("assets: %s, %lu ms", g_status.c_str(), elapsed);
    return g_loaded;
}

bool        Loaded() { return g_loaded; }
const char* Status() { return g_status.c_str(); }

const std::vector<Item>&        Items()      { return g_items; }
const std::vector<std::string>& Categories() { return g_categories; }

} // namespace mod::assets
