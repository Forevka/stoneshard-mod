#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace mod::sym {

struct Entry {
    const char* name;   // points into the game's own .rdata, stable for process lifetime
    void*       func;   // address in .text
};

struct SectionRange {
    std::uintptr_t lo = 0;
    std::uintptr_t hi = 0;
    bool contains(std::uintptr_t p) const { return p >= lo && p < hi; }
};

SectionRange TextRange();
SectionRange RdataRange();
SectionRange DataRange();

// All resolved entries, sorted by name.
const std::vector<Entry>& All();

// Name of the function containing `addr`, or nullptr. Uses an address-sorted
// index, so it turns a return address into "who called this".
const char* OwnerOf(const void* addr);

// Rebuilds the name -> function map by scanning the LOADED module's .data for
// YYC's registration rows. Nothing is hardcoded to an address, so this keeps
// working across game updates. Returns the health-check result.
bool Scan();

bool        Healthy();
const char* HealthMessage();

std::size_t Count();

// Exact lookup, e.g. "gml_Script_player_move".
void* Find(const std::string& name);

// Case-insensitive substring filter over all names.
std::vector<const Entry*> Search(const std::string& needle, std::size_t limit);

} // namespace mod::sym
