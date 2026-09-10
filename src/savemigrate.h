#pragma once

#include <string>
#include <vector>

namespace mod::savemigrate {

// Importing a save folder from another machine.
//
// Copying the files is NOT enough, and this is the whole reason the feature has
// to exist rather than being a drag-and-drop. Every save file is
//
//     zlib( <json text> <32 hex md5> <NUL> )
//
// and the md5 is salted with the file's own FOLDER PATH:
//
//     salt = "stOne!" + "!".join(path from characters_v1 down) + "!shArd"
//     md5(jsonText + salt)
//
// so `character_3/autosave_1/data.sav` and `character_1/autosave_1/data.sav`
// have different checksums for byte-identical content. A save dropped into a
// different slot number fails its own integrity check. Importing therefore
// means decompress -> re-hash against the NEW path -> recompress, per file.
//
// (Scheme origin: MikaBuchholz/stoneshard-editor. tools/checkcksum.py verifies
// it against every file in a live save directory rather than trusting it.)
//
// The good news is that there is no registry to update: characters.map holds
// only {lastCharacter, lastSave}, so the game finds characters by scanning for
// character_N folders. An import just has to produce well-formed folders.

// One importable character found in the source folder.
struct Character {
    std::string dir;        // absolute path of the source character_N folder
    std::string name;       // nameKey from character.map
    int         saves = 0;  // save slots inside it
    int         files = 0;  // .map/.sav files that will need re-signing
    bool        ok    = false;
    std::string note;       // why it was rejected, when !ok
};

// Opens the native folder picker.
//
// ON A WORKER THREAD, deliberately. The overlay draws on the game's own thread
// inside the Present hook, so a modal Windows dialog opened from here would
// block that thread and freeze the game behind its own dialog. The UI polls
// instead.
void Browse();
bool BrowseBusy();

const std::string& SourceDir();

// Re-scans SourceDir. Accepts either a characters_v1-style folder containing
// character_N subfolders, or a single character_N folder.
void Scan();

const std::vector<Character>& Found();
int         ImportableCount();
const char* Status();

// Where the game keeps saves: %LOCALAPPDATA%\StoneShard\characters_v1.
const std::string& TargetDir();

// Copies every importable character into a FREE slot number and re-signs it.
// Existing characters are never touched or overwritten.
bool Import();

void DrawSavesTab();

} // namespace mod::savemigrate
