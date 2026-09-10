#include "savemigrate.h"

#include "log.h"

#include <windows.h>
#include <bcrypt.h>
#include <shlobj.h>

#include <atomic>
#include <cstdio>
#include <filesystem>
#include <mutex>
#include <thread>

#include "zlib.h"
#include "imgui.h"

namespace fs = std::filesystem;

namespace mod::savemigrate {
namespace {

std::string              g_source;
std::string              g_target;
std::string              g_status = "Pick a folder to import from.";
std::vector<Character>   g_found;
std::atomic<bool>        g_browsing{false};
std::mutex               g_lock;          // guards g_source across the picker thread

void SetStatus(const char* fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    g_status = buf;
    Logf("saves: %s", buf);
}

// ------------------------------------------------------------------- crypto

std::string Md5Hex(const std::string& data) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_MD5_ALGORITHM, nullptr, 0) != 0) return {};

    unsigned char digest[16]{};
    BCRYPT_HASH_HANDLE hash = nullptr;
    bool ok = BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) == 0 &&
              BCryptHashData(hash, reinterpret_cast<PUCHAR>(const_cast<char*>(data.data())),
                             static_cast<ULONG>(data.size()), 0) == 0 &&
              BCryptFinishHash(hash, digest, sizeof(digest), 0) == 0;
    if (hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(alg, 0);
    if (!ok) return {};

    char out[33];
    for (int i = 0; i < 16; ++i) std::snprintf(out + i * 2, 3, "%02x", digest[i]);
    return std::string(out, 32);
}

// ------------------------------------------------------------------- zlib

bool Inflate(const std::string& in, std::string* out) {
    // Saves are small (~140 KB of JSON); growing a buffer beats streaming.
    uLongf cap = static_cast<uLongf>(in.size() * 8 + 65536);
    for (int attempt = 0; attempt < 8; ++attempt) {
        out->resize(cap);
        uLongf got = cap;
        const int rc = uncompress(reinterpret_cast<Bytef*>(&(*out)[0]), &got,
                                  reinterpret_cast<const Bytef*>(in.data()),
                                  static_cast<uLong>(in.size()));
        if (rc == Z_OK) { out->resize(got); return true; }
        if (rc != Z_BUF_ERROR) return false;
        cap *= 4;
    }
    return false;
}

bool Deflate(const std::string& in, std::string* out) {
    uLongf cap = compressBound(static_cast<uLong>(in.size()));
    out->resize(cap);
    uLongf got = cap;
    if (compress2(reinterpret_cast<Bytef*>(&(*out)[0]), &got,
                  reinterpret_cast<const Bytef*>(in.data()),
                  static_cast<uLong>(in.size()), Z_DEFAULT_COMPRESSION) != Z_OK)
        return false;
    out->resize(got);
    return true;
}

// ------------------------------------------------------------------- files

bool ReadFile(const fs::path& p, std::string* out) {
    std::FILE* f = _wfopen(p.c_str(), L"rb");
    if (!f) return false;
    std::fseek(f, 0, SEEK_END);
    const long n = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    if (n <= 0) { std::fclose(f); return false; }
    out->resize(static_cast<std::size_t>(n));
    const std::size_t got = std::fread(&(*out)[0], 1, static_cast<std::size_t>(n), f);
    std::fclose(f);
    out->resize(got);
    return got > 0;
}

bool WriteFile(const fs::path& p, const std::string& data) {
    std::FILE* f = _wfopen(p.c_str(), L"wb");
    if (!f) return false;
    const std::size_t put = std::fwrite(data.data(), 1, data.size(), f);
    std::fclose(f);
    return put == data.size();
}

// The JSON body of a save, without its checksum. Returns false for anything
// that is not a save file, which is how junk in the source folder is rejected
// rather than copied blindly.
bool ReadSaveJson(const fs::path& p, std::string* json) {
    std::string raw, plain;
    if (!ReadFile(p, &raw)) return false;
    if (!Inflate(raw, &plain)) return false;

    while (!plain.empty() && plain.back() == '\0') plain.pop_back();
    if (plain.size() < 33) return false;

    *json = plain.substr(0, plain.size() - 32);
    return true;
}

// salt = "stOne!" + "!".join(components from characters_v1 down) + "!shArd"
std::string SaltFor(const std::vector<std::string>& components) {
    std::string s = "stOne!characters_v1";
    for (const std::string& c : components) { s += '!'; s += c; }
    s += "!shArd";
    return s;
}

// Writes `json` to `p`, signed for the path it is being written TO.
bool WriteSigned(const fs::path& p, const std::string& json,
                 const std::vector<std::string>& components) {
    const std::string sum = Md5Hex(json + SaltFor(components));
    if (sum.size() != 32) return false;

    std::string body = json + sum;
    body.push_back('\0');

    std::string packed;
    if (!Deflate(body, &packed)) return false;
    return WriteFile(p, packed);
}

bool IsSaveFile(const fs::path& p) {
    const std::string ext = p.extension().string();
    return ext == ".map" || ext == ".sav";
}

std::string NameFromCharacterMap(const fs::path& charDir) {
    std::string json;
    if (!ReadSaveJson(charDir / "character.map", &json)) return {};

    // Deliberately not a JSON parser: one string field is wanted and the file
    // is machine-generated, so the key is found literally.
    const std::string key = "\"nameKey\":";
    const std::size_t k = json.find(key);
    if (k == std::string::npos) return {};
    const std::size_t q1 = json.find('"', k + key.size());
    if (q1 == std::string::npos) return {};
    const std::size_t q2 = json.find('"', q1 + 1);
    if (q2 == std::string::npos) return {};
    return json.substr(q1 + 1, q2 - q1 - 1);
}

// %LOCALAPPDATA%\StoneShard\characters_v1
std::string ResolveTarget() {
    PWSTR local = nullptr;
    if (SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local) != S_OK) return {};
    const fs::path p = fs::path(local) / L"StoneShard" / L"characters_v1";
    CoTaskMemFree(local);
    return p.string();
}

Character Inspect(const fs::path& dir) {
    Character c;
    c.dir  = dir.string();
    c.name = NameFromCharacterMap(dir);
    if (c.name.empty()) {
        c.note = "no readable character.map";
        return c;
    }

    std::error_code ec;
    for (const auto& e : fs::recursive_directory_iterator(dir, ec)) {
        if (!e.is_regular_file(ec)) continue;
        if (IsSaveFile(e.path())) ++c.files;
    }
    for (const auto& e : fs::directory_iterator(dir, ec))
        if (e.is_directory(ec)) ++c.saves;

    if (c.saves == 0) { c.note = "no save slots inside"; return c; }

    c.ok = true;
    return c;
}

// The lowest character_N not already present, so nothing is ever overwritten.
int FreeSlot(const fs::path& target, int from) {
    std::error_code ec;
    for (int i = from; i < 10000; ++i) {
        char name[32];
        std::snprintf(name, sizeof(name), "character_%d", i);
        if (!fs::exists(target / name, ec)) return i;
    }
    return -1;
}

} // namespace

const std::string& SourceDir() { std::lock_guard<std::mutex> g(g_lock); return g_source; }
const char*        Status()    { return g_status.c_str(); }
const std::vector<Character>& Found() { return g_found; }
bool               BrowseBusy() { return g_browsing.load(); }

const std::string& TargetDir() {
    if (g_target.empty()) g_target = ResolveTarget();
    return g_target;
}

int ImportableCount() {
    int n = 0;
    for (const Character& c : g_found) if (c.ok) ++n;
    return n;
}

// ------------------------------------------------------------------- browse

void Browse() {
    if (g_browsing.exchange(true)) return;      // one dialog at a time

    // Detached: the overlay runs inside the game's Present hook, and blocking
    // that thread on a modal dialog would freeze the game behind it.
    std::thread([] {
        std::string picked;

        if (SUCCEEDED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE))) {
            IFileOpenDialog* dlg = nullptr;
            if (SUCCEEDED(CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER,
                                           IID_PPV_ARGS(&dlg)))) {
                DWORD opts = 0;
                dlg->GetOptions(&opts);
                dlg->SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                dlg->SetTitle(L"Select the save folder to import");

                if (SUCCEEDED(dlg->Show(nullptr))) {
                    IShellItem* item = nullptr;
                    if (SUCCEEDED(dlg->GetResult(&item))) {
                        PWSTR path = nullptr;
                        if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path))) {
                            picked = fs::path(path).string();
                            CoTaskMemFree(path);
                        }
                        item->Release();
                    }
                }
                dlg->Release();
            }
            CoUninitialize();
        }

        if (!picked.empty()) {
            { std::lock_guard<std::mutex> g(g_lock); g_source = picked; }
            Scan();
        }
        g_browsing.store(false);
    }).detach();
}

// --------------------------------------------------------------------- scan

void Scan() {
    g_found.clear();

    const std::string src = SourceDir();
    if (src.empty()) { SetStatus("Pick a folder to import from."); return; }

    const fs::path root(src);
    std::error_code ec;
    if (!fs::is_directory(root, ec)) { SetStatus("Not a folder: %s", src.c_str()); return; }

    // Either a characters_v1-style folder full of character_N subfolders, or a
    // single character_N folder handed over on its own.
    if (fs::exists(root / "character.map", ec)) {
        g_found.push_back(Inspect(root));
    } else {
        for (const auto& e : fs::directory_iterator(root, ec)) {
            if (!e.is_directory(ec)) continue;
            if (e.path().filename().string().rfind("character_", 0) != 0) continue;
            g_found.push_back(Inspect(e.path()));
        }
    }

    const int ok = ImportableCount();
    if (g_found.empty())
        SetStatus("No character folders here. Point at a characters_v1 folder or a character_N folder.");
    else if (ok == 0)
        SetStatus("Found %zu folder(s), but none are importable.", g_found.size());
    else
        SetStatus("Found %d character%s ready to import.", ok, ok == 1 ? "" : "s");
}

// ------------------------------------------------------------------- import

bool Import() {
    const fs::path target(TargetDir());
    if (target.empty()) { SetStatus("Could not locate the game's save folder."); return false; }

    std::error_code ec;
    fs::create_directories(target, ec);

    int imported = 0, slot = 1;
    for (const Character& c : g_found) {
        if (!c.ok) continue;

        slot = FreeSlot(target, slot);
        if (slot < 0) { SetStatus("No free character slot."); return false; }

        char slotName[32];
        std::snprintf(slotName, sizeof(slotName), "character_%d", slot);
        const fs::path dest = target / slotName;

        const fs::path src(c.dir);
        bool failed = false;

        for (const auto& e : fs::recursive_directory_iterator(src, ec)) {
            if (!e.is_regular_file(ec)) continue;

            const fs::path rel = fs::relative(e.path(), src, ec);
            const fs::path out = dest / rel;
            fs::create_directories(out.parent_path(), ec);

            if (!IsSaveFile(e.path())) {
                // preview.png and anything else is not signed - copy verbatim.
                if (!fs::copy_file(e.path(), out, fs::copy_options::overwrite_existing, ec))
                    { failed = true; break; }
                continue;
            }

            std::string json;
            if (!ReadSaveJson(e.path(), &json)) { failed = true; break; }

            // The salt is the CONTAINING DIRECTORY relative to characters_v1,
            // using the NEW slot name - which is the entire point of
            // re-signing. The filename is NOT part of it:
            //     character_2/autosave_1/data.sav
            //         -> stOne!characters_v1!character_2!autosave_1!shArd
            std::vector<std::string> components{ slotName };
            for (const auto& part : rel.parent_path()) components.push_back(part.string());

            if (!WriteSigned(out, json, components)) { failed = true; break; }
        }

        if (failed) {
            fs::remove_all(dest, ec);          // never leave a half-written character
            SetStatus("Failed while importing %s - nothing was left behind.", c.name.c_str());
            return false;
        }

        Logf("saves: imported %s -> %s (%d file(s) re-signed)",
             c.name.c_str(), slotName, c.files);
        ++imported;
        ++slot;
    }

    if (imported == 0) { SetStatus("Nothing to import."); return false; }
    SetStatus("Imported %d character%s. Restart the game to see them.",
              imported, imported == 1 ? "" : "s");
    return true;
}

// ---------------------------------------------------------------------- tab

void DrawSavesTab() {
    ImGui::TextWrapped(
        "Import save folders copied from another machine. Every save file is signed with "
        "an MD5 salted by its own FOLDER PATH, so a save dropped into a different slot "
        "number fails its own checksum - each file is decompressed, re-signed for its new "
        "path, and recompressed.");
    ImGui::Spacing();

    ImGui::BeginDisabled(BrowseBusy());
    if (ImGui::Button("Select save folder...", ImVec2(220.0f, 0.0f))) Browse();
    ImGui::EndDisabled();
    ImGui::SameLine();
    if (BrowseBusy()) ImGui::TextDisabled("(dialog open)");
    else              ImGui::TextDisabled("a Windows folder picker");

    const std::string src = SourceDir();
    ImGui::Text("Source: %s", src.empty() ? "(none selected)" : src.c_str());
    ImGui::Text("Target: %s", TargetDir().c_str());

    ImGui::Spacing();
    ImGui::SeparatorText("Scan");
    ImGui::TextWrapped("%s", Status());

    if (!Found().empty()) {
        ImGui::BeginChild("##found", ImVec2(0.0f, 160.0f), true);
        for (const Character& c : Found()) {
            if (c.ok) {
                ImGui::TextColored(ImVec4(0.55f, 0.90f, 0.55f, 1.0f),
                                   "%-20s %d save slot(s), %d file(s) to re-sign",
                                   c.name.c_str(), c.saves, c.files);
            } else {
                ImGui::TextColored(ImVec4(0.95f, 0.65f, 0.55f, 1.0f),
                                   "%-20s skipped - %s",
                                   fs::path(c.dir).filename().string().c_str(), c.note.c_str());
            }
        }
        ImGui::EndChild();
    }

    ImGui::Spacing();
    const int n = ImportableCount();

    ImGui::BeginDisabled(n == 0);
    if (ImGui::Button("Import", ImVec2(220.0f, 0.0f))) Import();
    ImGui::EndDisabled();
    ImGui::SameLine();
    if (n == 0) ImGui::TextDisabled("select a folder with characters first");
    else        ImGui::TextDisabled("copies into free slots - existing characters are untouched");

    ImGui::Spacing();
    ImGui::TextDisabled(
        "Imported characters go into the lowest FREE character_N slots, so nothing you "
        "already have is overwritten. Restart the game afterwards to pick them up.");
}

} // namespace mod::savemigrate
