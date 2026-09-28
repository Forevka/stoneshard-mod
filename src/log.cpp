#include "log.h"
#include "paths.h"

#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <mutex>

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
    g_file = std::fopen(paths::File("coreloader.log").c_str(), "w");
}

void LogShutdown() {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_file) {
        std::fclose(g_file);
        g_file = nullptr;
    }
}

void Logf(const char* fmt, ...) {
    char body[1024];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(body, sizeof(body), fmt, args);
    va_end(args);

    std::string line = Timestamp() + "  " + body;

    std::lock_guard<std::mutex> lock(g_mutex);
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
