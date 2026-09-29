#include "items.h"

#include "gml.h"
#include "log.h"
#include "savebackup.h"
#include "symbols.h"

#include <cmath>
#include <cstdarg>
#include <cstdint>
#include <deque>
#include <cstdio>

namespace mod::items {
namespace {

std::string g_error;

void Fail(const char* fmt, ...) {
    char buf[256];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    g_error = buf;
    Logf("[!] items: %s", buf);
}

std::string Line(const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    return buf;
}

// Builtins are handed a `self` even when they ignore it, and a null one is not
// worth the risk. The instance being addressed travels in args[0], so `self`
// only has to be some live instance.
void* Context() {
    void* p = gml::PlayerInstance();
    return p ? p : gml::CurrentSelf();
}

// Resolved by NAME through the game's own asset lookup - no asset index is
// ever compiled in, so a patch that renumbers objects changes nothing here.
bool ObjectIndex(const std::string& objectName, double* out) {
    gml::RValue nameArg{};
    if (!gml::SetString(nameArg, gml::Intern(objectName))) {
        Fail("could not build the object name string");
        return false;
    }

    gml::RValue idx{};
    if (!builtins::Call("asset_get_index", &idx, &nameArg, 1, Context()) ||
        idx.kind != gml::kReal || idx.real < 0.0) {
        Fail("unknown object '%s' (asset_get_index kind=%d)", objectName.c_str(), idx.kind);
        return false;
    }
    *out = idx.real;
    return true;
}

} // namespace

const char* LastError() { return g_error.c_str(); }

// ------------------------------------------------------------------- spawning

const char* RarityName(int rarity) {
    switch (rarity) {
    case kCommon:   return "Common";
    case kUncommon: return "Uncommon";
    case kRare:     return "Rare";
    case kEpic:     return "Epic";
    case kCursed:   return "Cursed";
    case kUnique:   return "Unique";
    case kTreasure: return "Treasure";
    default:        return "?";
    }
}

namespace {

// Item names are handed over interned (gml::Intern): SetString does not copy -
// it points a RefString at the characters, flags them external, and the game
// stores that pointer inside the item it builds, for good. One shared buffer
// reassigned per spawn would rewrite the names of items already made.

// A recorded call is replayed from raw bits, and the game freed any string,
// array or struct among them after its own call: those would dangle.
bool ReplayableArgs(const std::vector<gml::RValue>& raw, std::size_t skip) {
    for (std::size_t i = 0; i < raw.size(); ++i) {
        if (i == skip) continue;
        const std::int32_t k = raw[i].kind & 0x00FFFFFF;
        if (k == gml::kString || k == gml::kArray || k == gml::kObject) return false;
    }
    return true;
}

// Takes the instance scr_weapon_loot returned, if it gave us one.
bool HandleFromResult(const gml::RValue& result, builtins::Handle* out) {
    if (!out) return false;
    if (result.kind != gml::kRef) return false;

    out->id      = result;
    out->self    = Context();
    out->haveRef = true;
    return true;
}

} // namespace

bool SpawnGear(const std::string& displayName, double dx, double dy,
               int rarity, builtins::Handle* created) {
    backup::EnsureBackupOnce();

    void* fn = sym::Find("gml_Script_scr_weapon_loot");
    if (!fn) { Fail("scr_weapon_loot not found"); return false; }

    double px = 0.0, py = 0.0;
    const bool  havePos    = gml::PlayerPosition(px, py);
    void* const player     = gml::PlayerInstance();
    const auto& rec        = gml::WeaponRecord();
    const bool  haveSample = rec.valid && rec.raw.size() >= 5 && rec.self && ReplayableArgs(rec.raw, 0);

    if (!player && !haveSample) {
        Fail("no player instance and no recorded call to replay");
        return false;
    }

    const char* held = gml::Intern(displayName);

    std::vector<gml::RValue> args;
    void* self  = nullptr;
    void* other = nullptr;

    if (player) {
        // scr_weapon_loot(name, x, y, chance, rarity). With x/y at 0 the game
        // falls back to the tile you are standing on, so an uncalibrated
        // position still spawns - it just lands less precisely.
        args.assign(5, gml::RValue{});
        gml::SetString(args[0], held);
        gml::SetReal(args[1], havePos ? px + dx : 0.0);
        gml::SetReal(args[2], havePos ? py + dy : 0.0);
        gml::SetReal(args[3], 100.0);                          // chance -> certain
        gml::SetReal(args[4], static_cast<double>(rarity));    // <- the rarity roll
        self = other = player;
    } else {
        // Fallback: replay a genuine call the game made. Drops wherever that
        // call was headed, which is why it is second choice.
        args = rec.raw;
        gml::SetString(args[0], held);
        gml::SetReal(args[3], 100.0);
        gml::SetReal(args[4], static_cast<double>(rarity));
        self  = rec.self;
        other = rec.other;
    }

    std::vector<gml::RValue*> argv(args.size());
    for (std::size_t i = 0; i < args.size(); ++i) argv[i] = &args[i];

    gml::RValue result{};
    if (!gml::CallAs(fn, &result, argv.data(), static_cast<int>(args.size()), self, other)) {
        Fail("scr_weapon_loot failed%s%s", gml::LastError()[0] ? ": " : "", gml::LastError());
        return false;
    }

    HandleFromResult(result, created);
    Logf("items: spawned %s as %s", displayName.c_str(), RarityName(rarity));
    return true;
}

bool AddWeaponToInventory(const std::string& displayName, int rarity) {
    backup::EnsureBackupOnce();

    void* fn = sym::Find("gml_Script_scr_inventory_add_weapon");
    if (!fn) { Fail("scr_inventory_add_weapon not found"); return false; }

    void* player = gml::PlayerInstance();
    if (!player) { Fail("no player instance"); return false; }

    gml::RValue args[2]{};
    gml::SetString(args[0], gml::Intern(displayName));
    gml::SetReal(args[1], static_cast<double>(rarity));

    gml::RValue* argv[2] = { &args[0], &args[1] };
    gml::RValue  result{};
    if (!gml::CallAs(fn, &result, argv, 2, player, player)) {
        Fail("scr_inventory_add_weapon failed%s%s",
             gml::LastError()[0] ? ": " : "", gml::LastError());
        return false;
    }
    Logf("items: added %s (%s) straight to the inventory",
         displayName.c_str(), RarityName(rarity));
    return true;
}

// ---------------------------------------------------------------- instances

int InstanceCount(const std::string& objectName) {
    double idx = 0.0;
    if (!ObjectIndex(objectName, &idx)) return -1;

    gml::RValue arg{};
    gml::SetReal(arg, idx);

    gml::RValue r{};
    if (!builtins::Call("instance_number", &r, &arg, 1, Context()) || r.kind != gml::kReal)
        return -1;
    return static_cast<int>(r.real);
}

bool NearestInstance(const std::string& objectName, builtins::Handle* out, double* dist) {
    double idx = 0.0;
    if (!ObjectIndex(objectName, &idx)) return false;

    double px = 0.0, py = 0.0;
    if (!gml::PlayerPosition(px, py)) {
        Fail("player position unknown, cannot find the nearest %s", objectName.c_str());
        return false;
    }

    gml::RValue args[3]{};
    gml::SetReal(args[0], px);
    gml::SetReal(args[1], py);
    gml::SetReal(args[2], idx);

    gml::RValue inst{};
    if (!builtins::Call("instance_nearest", &inst, args, 3, Context())) {
        Fail("instance_nearest failed for %s", objectName.c_str());
        return false;
    }
    // noone (-4) comes back as a plain real; a live instance as a reference.
    if (inst.kind == gml::kReal && inst.real < 0.0) {
        Fail("no %s instance exists", objectName.c_str());
        return false;
    }
    if (inst.kind != gml::kRef && inst.kind != gml::kReal) {
        Fail("instance_nearest returned an unusable handle (kind=%d)", inst.kind);
        return false;
    }

    out->id      = inst;
    out->self    = Context();
    out->haveRef = true;

    *dist = -1.0;
    gml::RValue ix{}, iy{};
    if (builtins::GetVar(*out, "x", &ix) && builtins::GetVar(*out, "y", &iy) &&
        ix.kind == gml::kReal && iy.kind == gml::kReal) {
        const double ddx = ix.real - px, ddy = iy.real - py;
        *dist = std::sqrt(ddx * ddx + ddy * ddy);
    }
    return true;
}

// Addresses instance number `n` of an object. instance_find hands back a
// reference (kind 15) rather than a numeric id, which is the whole reason the
// reference is passed straight back through and never decoded.
bool InstanceAt(double objectIndex, int nth, builtins::Handle* out) {
    gml::RValue args[2]{};
    gml::SetReal(args[0], objectIndex);
    gml::SetReal(args[1], static_cast<double>(nth));

    gml::RValue inst{};
    if (!builtins::Call("instance_find", &inst, args, 2, Context())) return false;
    if (inst.kind == gml::kReal && inst.real < 0.0) return false;
    if (inst.kind != gml::kRef && inst.kind != gml::kReal) return false;

    out->id      = inst;
    out->self    = Context();
    out->haveRef = true;
    return true;
}

// Identity of an instance, for telling "the one we just made" from the pile of
// earlier drops on the same tile.
//
// NOT the `id` variable: this runtime answers instance queries with a
// REFERENCE (kind 15), not a number - the same trap builtins.h documents, and
// reading `id` as a real is what made the first attempt find nothing at all.
// The reference's own bits are the identity, compared but never decoded.
std::int64_t RefBits(const builtins::Handle& h) { return h.id.i64; }

bool FirstInstanceImpl(const std::string& objectName, builtins::Handle* out) {
    double idx = 0.0;
    if (!ObjectIndex(objectName, &idx)) return false;

    const int count = InstanceCount(objectName);
    if (count <= 0) { Fail("no %s instance exists", objectName.c_str()); return false; }
    if (!InstanceAt(idx, 0, out)) { Fail("could not address %s", objectName.c_str()); return false; }
    return true;
}

bool CollectRefs(const std::string& objectName, std::vector<std::int64_t>* out) {
    out->clear();

    double idx = 0.0;
    if (!ObjectIndex(objectName, &idx)) return false;

    const int count = InstanceCount(objectName);
    if (count < 0) return false;

    for (int i = 0; i < count; ++i) {
        builtins::Handle h{};
        if (InstanceAt(idx, i, &h)) out->push_back(RefBits(h));
    }
    return true;
}

// Picks the instance whose reference was not present before the spawn.
// `newCount` reports how many qualified: exactly 1 means the identification is
// unambiguous, and anything else is said out loud rather than papered over,
// because silently reading the WRONG item is how the previous probe run
// reported a Royal Blade that was really a Militia Falchion.
bool NewInstance(const std::string& objectName, const std::vector<std::int64_t>& before,
                 builtins::Handle* out, int* newCount) {
    *newCount = 0;

    double idx = 0.0;
    if (!ObjectIndex(objectName, &idx)) return false;

    const int count = InstanceCount(objectName);
    if (count < 0) return false;

    bool found = false;
    for (int i = 0; i < count; ++i) {
        builtins::Handle h{};
        if (!InstanceAt(idx, i, &h)) continue;

        const std::int64_t bits = RefBits(h);
        bool seen = false;
        for (std::int64_t b : before) if (b == bits) { seen = true; break; }
        if (seen) continue;

        ++*newCount;
        *out  = h;              // last one wins: creation order puts ours last
        found = true;
    }

    if (!found) Fail("no new %s instance appeared (%d exist)", objectName.c_str(), count);
    return found;
}

// -------------------------------------------------------------------- dumping

namespace {

// An item's stats do not sit on the instance - they live in ds_maps it points
// at, and those ids are RECYCLED within seconds of the item being picked up.
// So a map is expanded here, in the same pass that reads the field, rather
// than handed back as a number to chase later.
//
// json_encode on a stale or non-map id returns a null string rather than
// faulting, which makes the "try it and see" test safe.
bool MapJson(double id, std::string* out) {
    gml::RValue arg{};
    gml::SetReal(arg, id);

    gml::RValue r{};
    if (!builtins::Call("json_encode", &r, &arg, 1, Context()) || r.kind != gml::kString)
        return false;

    *out = gml::ToString(r);
    return !out->empty() && *out != "<null string>";
}

// ds_lists need enumerating rather than encoding: json_encode only understands
// a ds_map and answers a null string for anything else. Entries that are
// themselves map handles get expanded in turn, which is how a catalogue of
// curses would be stored.
bool ListDump(double id, std::vector<std::string>* out) {
    gml::RValue arg{};
    gml::SetReal(arg, id);

    gml::RValue size{};
    if (!builtins::Call("ds_list_size", &size, &arg, 1, Context()) || size.kind != gml::kReal)
        return false;

    const int count = static_cast<int>(size.real);
    if (count <= 0 || count > 4096) return false;

    for (int i = 0; i < count; ++i) {
        gml::RValue args[2]{};
        gml::SetReal(args[0], id);
        gml::SetReal(args[1], static_cast<double>(i));

        gml::RValue v{};
        if (!builtins::Call("ds_list_find_value", &v, args, 2, Context())) break;

        out->push_back(Line("        [%d] kind=%-3d %s", i, v.kind, gml::ToString(v).c_str()));

        // An entry that is itself a map is where the interesting shape lives.
        std::string json;
        if (v.kind == gml::kReal && v.real >= 1.0 && v.real < 1e9 && MapJson(v.real, &json))
            out->push_back("            " + json);
    }
    return true;
}

// Worth expanding? Only integral, plausible handles on fields whose name says
// they hold a structure - guessing wider would json_encode arbitrary numbers.
bool LooksLikeMap(const std::string& name, const gml::RValue& v) {
    if (v.kind != gml::kReal) return false;
    if (v.real < 1.0 || v.real > 1e9 || v.real != static_cast<double>(static_cast<long long>(v.real)))
        return false;

    auto has = [&](const char* frag) { return name.find(frag) != std::string::npos; };
    return has("map") || has("Map") || has("data") || has("Data") ||
           has("list") || has("List");
}

// Reply() formats through a fixed buffer, so a long JSON blob has to arrive as
// several lines or it is silently cut off.
void PushWrapped(std::vector<std::string>& out, const std::string& text, std::size_t width) {
    for (std::size_t i = 0; i < text.size(); i += width)
        out.push_back("        " + text.substr(i, width));
}

} // namespace

std::vector<std::string> DumpVars(const builtins::Handle& h, int limit, int* total) {
    std::vector<std::string> lines;

    std::vector<std::string> names;
    const int n = builtins::VarNames(h, names, limit);
    if (total) *total = n;
    if (n < 0) return lines;

    lines.reserve(names.size());
    for (const std::string& name : names) {
        gml::RValue v{};
        if (!builtins::GetVar(h, name.c_str(), &v)) {
            lines.push_back(Line("   %-34s <unreadable>", name.c_str()));
            continue;
        }

        lines.push_back(Line("   %-34s kind=%-3d %s", name.c_str(), v.kind,
                             gml::ToString(v).c_str()));

        if (!LooksLikeMap(name, v)) continue;

        std::string json;
        if (MapJson(v.real, &json)) { PushWrapped(lines, json, 220); continue; }

        // Not a map - try it as a list before giving up on it.
        ListDump(v.real, &lines);
    }
    return lines;
}

// ---------------------------------------------------------------------- probe

// ------------------------------------------------------------- constructor

namespace {

std::vector<Field> g_template;
std::string        g_templateName;

// Strings handed to the runtime are NOT copied - gml::SetString points a
// RefString at our buffer and flags it external, and the game keeps that
// pointer. Every key and string value we ever write therefore has to outlive
// the call permanently. A deque never invalidates what it already holds.
const char* Intern(const std::string& s) {
    static std::deque<std::string> pool;
    pool.push_back(s);
    return pool.back().c_str();
}

// The item's `data` map, which is where everything lives.
bool DataMap(const builtins::Handle& item, double* mapId) {
    gml::RValue d{};
    if (!builtins::GetVar(item, "data", &d) || d.kind != gml::kReal) return false;
    *mapId = d.real;
    return true;
}

// GML truth, as the runtime spells it: a bool keeps its value in i32, a real
// in the double. Reading a bool through i64 yields the DOUBLE'S BIT PATTERN -
// which is how "identified": 1 came back as 4607182418800017408.
bool Truthy(const gml::RValue& v) {
    switch (v.kind) {
    case gml::kBool:  return v.i32 != 0;
    case gml::kReal:  return v.real != 0.0;
    case gml::kInt32: return v.i32 != 0;
    case gml::kInt64: return v.i64 != 0;
    default:          return false;
    }
}

// Which keys hold a NESTED ds_list/ds_map rather than a scalar.
//
// This matters more than it looks. `Curse` and `Main` are nested lists;
// ds_map_find_value hands their raw ds id back as a plain number (9977, 9978).
// Storing that and writing it back with ds_map_replace destroys the nesting,
// leaving "Main": 9977.0 where [ "Slashing_Damage", 19.0 ] belonged - which is
// what a constructed staff came back with.
//
// The obvious test, ds_map_is_list, DOES NOT WORK here: on a pristine
// game-spawned item it answers false for a key that json_encode renders as an
// array and that ds_list_size reports as 2 entries. It is registered with the
// right arity and simply returns the wrong answer - another stub to route
// around, like the scr_console_* commands.
//
// ds_exists(value, ds_type_list) would work but collides: a legitimate
// "Duration": 94 could match a live ds id and get silently dropped.
//
// So json_encode decides, since it demonstrably gets this right. Only the
// shape is inspected - "key": [ or "key": { - which needs no JSON parser.
std::vector<std::string> NestedKeys(double mapId) {
    std::vector<std::string> keys;

    std::string json;
    if (!MapJson(mapId, &json)) return keys;

    for (std::size_t i = 0; i + 1 < json.size(); ++i) {
        if (json[i] != '"') continue;
        const std::size_t close = json.find('"', i + 1);
        if (close == std::string::npos) break;

        const std::string key = json.substr(i + 1, close - i - 1);
        i = close;

        // Must be followed by ": " and then a bracket to count.
        std::size_t j = close + 1;
        if (j >= json.size() || json[j] != ':') continue;
        ++j;
        while (j < json.size() && json[j] == ' ') ++j;
        if (j < json.size() && (json[j] == '[' || json[j] == '{')) keys.push_back(key);
    }
    return keys;
}

// Walks the map with the game's own iterator rather than parsing json_encode
// output: the keys and values arrive as RValues, so a string stays a string
// and a number stays a number with nothing to re-parse.
bool ReadFields(double mapId, std::vector<Field>* out) {
    out->clear();

    gml::RValue mapArg{};
    gml::SetReal(mapArg, mapId);

    const std::vector<std::string> nested = NestedKeys(mapId);

    gml::RValue key{};
    if (!builtins::Call("ds_map_find_first", &key, &mapArg, 1, Context())) return false;

    for (int guard = 0; guard < 512; ++guard) {
        if (key.kind == gml::kUndefined || key.kind == gml::kUnset) break;

        gml::RValue args[2]{};
        args[0] = mapArg;
        args[1] = key;

        // Nested lists and maps never become editable fields, so they are also
        // never written back - which is what keeps them intact.
        const std::string keyName = gml::ToString(key);
        bool structural = false;
        for (const std::string& nk : nested) if (nk == keyName) { structural = true; break; }

        if (structural) {
            gml::RValue skip{};
            if (!builtins::Call("ds_map_find_next", &skip, args, 2, Context())) break;
            key = skip;
            continue;
        }

        gml::RValue val{};
        if (!builtins::Call("ds_map_find_value", &val, args, 2, Context())) break;

        Field f;
        f.key = keyName;

        if (val.kind == gml::kReal) {
            f.isString = false;
            f.num      = val.real;
            out->push_back(f);
        } else if (val.kind == gml::kBool) {
            f.isString = false;
            f.num      = Truthy(val) ? 1.0 : 0.0;      // NOT val.i64 - see Truthy
            out->push_back(f);
        } else if (val.kind == gml::kInt32 || val.kind == gml::kInt64) {
            f.isString = false;
            f.num      = static_cast<double>(val.kind == gml::kInt32 ? val.i32 : val.i64);
            out->push_back(f);
        } else if (val.kind == gml::kString) {
            f.isString = true;
            f.str      = gml::ToString(val);
            out->push_back(f);
        }

        gml::RValue next{};
        if (!builtins::Call("ds_map_find_next", &next, args, 2, Context())) break;
        key = next;
    }
    return !out->empty();
}

bool WriteField(double mapId, const Field& f) {
    gml::RValue args[3]{};
    gml::SetReal(args[0], mapId);
    if (!gml::SetString(args[1], Intern(f.key))) return false;

    if (f.isString) {
        if (!gml::SetString(args[2], Intern(f.str))) return false;
    } else {
        gml::SetReal(args[2], f.num);
    }

    // replace, not add: it sets an existing key and creates a missing one,
    // which is exactly the "a new stat IS an enchantment" case.
    gml::RValue r{};
    return builtins::Call("ds_map_replace", &r, args, 3, Context());
}

bool DestroyInstance(const builtins::Handle& h) {
    gml::RValue r{};
    gml::RValue arg = h.id;
    return builtins::Call("instance_destroy", &r, &arg, 1, Context());
}

} // namespace

bool FirstInstance(const std::string& objectName, builtins::Handle* out) {
    return FirstInstanceImpl(objectName, out);
}

bool                      HaveTemplate()  { return !g_template.empty(); }
const std::vector<Field>& Template()      { return g_template; }
const std::string&        TemplateName()  { return g_templateName; }

bool LoadTemplate(const std::string& displayName, int rarity) {
    g_template.clear();
    g_templateName.clear();
    g_error.clear();

    // scr_weapon_loot hands back the instance it made, so there is nothing to
    // hunt for. The reference-diffing fallback stays for the case where the
    // return value is not a usable reference.
    builtins::Handle h{};
    std::vector<std::int64_t> before;
    CollectRefs("o_weapon_loot", &before);

    if (!SpawnGear(displayName, 48.0, 0.0, rarity, &h)) return false;

    if (!h.haveRef) {
        int fresh = 0;
        if (!NewInstance("o_weapon_loot", before, &h, &fresh)) return false;
    }

    double mapId = 0.0;
    if (!DataMap(h, &mapId)) {
        Fail("the spawned %s has no data map", displayName.c_str());
        DestroyInstance(h);
        return false;
    }

    const bool ok = ReadFields(mapId, &g_template);

    // The template item is scaffolding, not a gift: take it back off the floor.
    DestroyInstance(h);

    if (!ok) { Fail("could not read the data map of %s", displayName.c_str()); return false; }

    g_templateName = displayName;
    Logf("items: template for %s -> %zu fields", displayName.c_str(), g_template.size());
    return true;
}

bool SpawnConfigured(const std::string& displayName, const std::vector<Field>& fields,
                     int rarity) {
    g_error.clear();

    builtins::Handle h{};
    std::vector<std::int64_t> before;
    CollectRefs("o_weapon_loot", &before);

    if (!SpawnGear(displayName, 48.0, 0.0, rarity, &h)) return false;

    if (!h.haveRef) {
        int fresh = 0;
        if (!NewInstance("o_weapon_loot", before, &h, &fresh)) return false;
    }

    double mapId = 0.0;
    if (!DataMap(h, &mapId)) { Fail("spawned item has no data map"); return false; }

    int written = 0;
    for (const Field& f : fields) if (WriteField(mapId, f)) ++written;

    Logf("items: configured %s - %d of %zu field(s) written",
         displayName.c_str(), written, fields.size());

    if (written != static_cast<int>(fields.size())) {
        Fail("only %d of %zu fields were written", written, fields.size());
        return false;
    }
    return true;
}

std::vector<std::string> ScanInventory(int limit) {
    std::vector<std::string> out;
    g_error.clear();

    double idx = 0.0;
    if (!ObjectIndex("o_inv_slot", &idx)) {
        out.push_back(Line("scan FAILED: %s", LastError()));
        return out;
    }

    const int count = InstanceCount("o_inv_slot");
    out.push_back(Line("=== %d o_inv_slot instance(s) ===", count));
    if (count <= 0) return out;

    const int cap = (limit > 0 && limit < count) ? limit : count;
    for (int i = 0; i < cap; ++i) {
        builtins::Handle h{};
        if (!InstanceAt(idx, i, &h)) { out.push_back(Line("[%d] unaddressable", i)); continue; }

        gml::RValue d{};
        if (!builtins::GetVar(h, "data", &d) || d.kind != gml::kReal) {
            out.push_back(Line("[%d] no data map", i));
            continue;
        }

        std::string json;
        if (!MapJson(d.real, &json)) {
            out.push_back(Line("[%d] data=%.0f not encodable", i, d.real));
            continue;
        }

        // Print everything, but make the enchanted ones findable at a glance:
        // an item with a populated Curse array or a rarity above Common is the
        // only kind that can teach us the entry format.
        const bool interesting = json.find("\"Curse\": [ ]") == std::string::npos ||
                                 json.find("\"is_cursed\": true") != std::string::npos ||
                                 (json.find("\"rarity\"") != std::string::npos &&
                                  json.find("\"rarity\": \"Common\"") == std::string::npos);

        out.push_back(Line("[%d] %s", i, interesting ? "<-- ENCHANTED/RARE" : ""));
        PushWrapped(out, json, 220);
    }
    return out;
}

std::vector<std::string> Probe(const std::string& displayName, int limit) {
    std::vector<std::string> out;
    g_error.clear();

    // The two carriers. scr_weapon_loot produces the ground drop; picking it up
    // produces an o_inv_slot - which is what the drop's own `inv_object` field
    // (4769) names. o_inv_weapon_slot is NOT it: that is the inventory GUI
    // widget, and it probed 7000+ pixels away.
    static const char* const kCarriers[] = { "o_weapon_loot", "o_inv_slot" };

    // Proximity cannot identify the drop we just made: every probe lands an
    // item on the same tile, so instance_nearest keeps returning the FIRST one
    // and a second probe silently re-reads the first item's data. Diff the
    // instance ids instead - the one that was not there before is ours.
    std::vector<std::int64_t> before[2];
    for (int i = 0; i < 2; ++i) {
        const bool ok = CollectRefs(kCarriers[i], &before[i]);
        out.push_back(Line("before: %-16s %d instance(s)%s", kCarriers[i],
                           static_cast<int>(before[i].size()), ok ? "" : "  (enumeration failed)"));
    }

    if (!SpawnGear(displayName, 48.0, 0.0)) {
        out.push_back(Line("spawn FAILED: %s", LastError()));
        return out;
    }
    out.push_back(Line("spawned \"%s\"", displayName.c_str()));

    for (int i = 0; i < 2; ++i) {
        const char* obj = kCarriers[i];

        builtins::Handle h{};
        int              fresh = 0;
        if (!NewInstance(obj, before[i], &h, &fresh)) {
            out.push_back(Line("=== %s: %s ===", obj, LastError()));
            continue;
        }

        int total = 0;
        auto vars = DumpVars(h, limit, &total);
        out.push_back(Line("=== %s  %d new instance(s)%s  %d instance variables ===",
                           obj, fresh,
                           fresh == 1 ? "" : "  <-- AMBIGUOUS, reading the last",
                           total));
        if (vars.empty())
            out.push_back(Line("   reflection FAILED on this instance"));
        else
            out.insert(out.end(), vars.begin(), vars.end());
    }

    return out;
}

} // namespace mod::items
