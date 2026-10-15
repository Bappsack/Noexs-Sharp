/**
 * @file encoder.h
 * @brief Encoding of memory chunks for the wire, spread over all CPU cores.
 *
 * Chunk format (unchanged): u8 compressed, u32 len, [u32 compressedLen], data.
 * RLE data is a list of (value, count) byte pairs; a chunk that RLE doesn't shrink is sent raw.
 */
#pragma once

#include <switch.h>

namespace Gecko {

    /// Space one encoded chunk of up to GECKO_BUFFER_SIZE bytes needs (header + data), padded.
    constexpr u32 ENCODED_CHUNK_STRIDE = 0x2040;

    /// Encodes one chunk into out (at least 9 + len bytes). Returns the encoded size.
    u32 encodeChunk(const u8 *src, u32 len, bool compress, u8 *out);

    /// Encodes src (total bytes, at most GECKO_STAGE_SIZE) as consecutive GECKO_BUFFER_SIZE chunks.
    /// Chunk i ends up at the returned buffer + i * ENCODED_CHUNK_STRIDE with size lens[i].
    /// The chunks are spread over the command thread and worker threads on the other cores
    /// (idle while a search dump has the game paused). Only one caller at a time (shared lock).
    const u8 *encodeChunks(const u8 *src, u32 total, bool compress, const u32 **lens);

    /// How many threads encode chunks (1 when the worker threads couldn't be started).
    int encoderThreads();
}
