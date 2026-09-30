#include "log.h"
#include "paths.h"

#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>
#include <unordered_map>

namespace mod {
namespace {

constexpr size_t kRingCapacity = 512;

std::mutex             g_mutex;
FILE*                  g_file = nullptr;
std::deque<std::string> g_ring;

std::string Timestamp() {
    SYSTEMTIME st{};
    GetLocalTime(&st);
    char buf[32];
    std::snprintf(buf, sizeof(buf), "%02u:%02u:%02u.%03u",
                  st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    return buf;
}

} // namespace

void LogInit() {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_file) return;
    // The previous session's log survives one launch: after a crash, the next
    // start must not wipe the only trail of what happened.
    const std::string path = paths::File("lodestone.log");
    const std::string prev = paths::File("lodestone.prev.log");
    MoveFileExA(path.c_str(), prev.c_str(), MOVEFILE_REPLACE_EXISTING);
    g_file = std::fopen(path.c_str(), "w");
}

void LogShutdown() {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_file) {
        std::fclose(g_file);
        g_file = nullptr;
    }
}

// A failure repeated every frame must not flood the log (and flush it 60 times
// a second on the game thread). The same line - same text - is written at most
// kBurst times a second; repeats are counted and summarised once allowed.
// Distinct lines (a burst of "hooked #1..#110" at start-up) are unaffected.
constexpr int kBurst = 5;

struct SiteBudget {
    ULONGLONG window = 0;
    int       used = 0;
    int       dropped = 0;
};

// Returns false if this line is over its budget. Under g_mutex.
bool Admit(const std::string& text, int& droppedBefore) {
    static std::unordered_map<std::string, SiteBudget> sites;
    if (sites.size() > 4096) sites.clear();   // distinct lines only ever pass anyway
    SiteBudget& s = sites[text];
    const ULONGLONG now = GetTickCount64();
    droppedBefore = 0;
    if (now - s.window >= 1000) {
        droppedBefore = s.dropped;
        s.window = now;
        s.used = 0;
        s.dropped = 0;
    }
    if (s.used >= kBurst) { ++s.dropped; return false; }
    ++s.used;
    return true;
}

void Logf(const char* fmt, ...) {
    char body[1024];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(body, sizeof(body), fmt, args);
    va_end(args);

    std::lock_guard<std::mutex> lock(g_mutex);
    int dropped = 0;
    if (!Admit(body, dropped)) return;

    std::string line = Timestamp() + "  " + body;
    if (dropped > 0) line += "   (+" + std::to_string(dropped) + " similar lines suppressed)";
    if (g_file) {
        std::fputs(line.c_str(), g_file);
        std::fputc('\n', g_file);
        std::fflush(g_file);   // flushed every line: a crash must not lose the trail
    }
    g_ring.push_back(std::move(line));
    if (g_ring.size() > kRingCapacity)
        g_ring.pop_front();
}

std::vector<std::string> LogSnapshot() {
    std::lock_guard<std::mutex> lock(g_mutex);
    return {g_ring.begin(), g_ring.end()};
}

} // namespace mod
