/**
 * @file gecko.h
 * @brief Switch useful tools.
 * @copyright your dad
 */
#pragma once

#include <switch.h>

#include "debug.h"
#include "transport.h"

// The App and the sysmodule must have the same major version (the App checks it on connect).
#define VER_MAJOR (2)
#define VER_MINOR (0)
#define VER_PATCH (1)

/// Size of one (optionally RLE compressed) data chunk on the wire; must match
/// the App's 8 KiB chunk buffer.
#define GECKO_BUFFER_SIZE (2048 * 4)

/// Game memory is read in blocks of this size and then sent as several wire chunks;
/// fewer svcReadDebugProcessMemory calls for the same memory footprint class.
#define GECKO_STAGE_SIZE (0x10000)

namespace Gecko {

    enum Status {
        Stopping,
        Running,
        Paused,
        Searching
    };

    /// State shared by all sessions (TCP and USB). Commands run with `lock` held.
    struct Shared {
        Debugger dbg;
        Status status;
        Mutex lock;
        int activeSessions;
        bool exit;
        u8 buffer[GECKO_BUFFER_SIZE];  // scratch for pids / small transfers
        u8 stage[GECKO_STAGE_SIZE];    // memory read staging
    };

    /// One client session on one transport.
    struct Context {
        Transport *conn;
        Shared *shared;
        bool stop; // set by the disconnect command
    };
};

void logString(const char *str, int len);

/// Copies the most recent log text (kept in memory, also without an SD card) into out.
u32 copyRecentLog(char *out, u32 max);

#define printf(fmt, ...) {                                          \
    char buffer[2048];                                              \
    logString(buffer, snprintf(buffer, 2048, fmt, ##__VA_ARGS__));  \
}

/// Executes one command (already read from the stream) and writes its result code.
Result cmd_decode(Gecko::Context &ctx, int cmd);
