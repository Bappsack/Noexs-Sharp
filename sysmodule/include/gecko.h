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

/// Game memory is read in blocks of this size and then encoded as several wire chunks in
/// parallel; fewer svcReadDebugProcessMemory calls and enough chunks to keep every core busy.
#define GECKO_STAGE_SIZE (0x20000)

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

    /// Transfer options a client can set with command 0x17 (bit flags).
    enum Option {
        OptionCompression     = 1, // compress memory chunks (otherwise sent raw)
        OptionDoubleBuffering = 2, // send one buffer while the next is filled
        OptionLz4             = 4, // with OptionCompression: LZ4 instead of RLE
    };

    /// How a memory chunk is encoded on the wire (its first byte).
    enum Codec : u8 {
        CodecRaw = 0,
        CodecRle = 1,
        CodecLz4 = 2,
    };

    /// One client session on one transport.
    struct Context {
        Transport *conn;
        Shared *shared;
        bool stop; // set by the disconnect command
        Codec codec = CodecRle;
        // memory read statistics since the last command 0x18
        u64 readTicks = 0;
        u64 compressTicks = 0;
        u64 memBytes = 0;
    };
};

void logString(const char *str, int len);

/// Timestamped line in the noexs log (SD card file and the in-memory copy).
void noexsLog(const char *fmt, ...);

/// Copies the most recent log text (kept in memory, also without an SD card) into out.
u32 copyRecentLog(char *out, u32 max);

#define printf(fmt, ...) {                                          \
    char buffer[2048];                                              \
    logString(buffer, snprintf(buffer, 2048, fmt, ##__VA_ARGS__));  \
}

/// Executes one command (already read from the stream) and writes its result code.
Result cmd_decode(Gecko::Context &ctx, int cmd);
