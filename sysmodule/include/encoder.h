/**
 * @file encoder.h
 * @brief Encoding of memory chunks for the wire, spread over all CPU cores.
 *
 * Chunk format: u8 codec, u32 len, [u32 encodedLen, encoded data] or [len raw bytes for codec 0].
 *  - codec 0 (raw): the data as is
 *  - codec 1 (RLE): a list of (value, count) byte pairs; chunks of up to GECKO_BUFFER_SIZE bytes
 *  - codec 2 (LZ4): one standard LZ4 block; chunks of up to LZ4_CHUNK_SIZE bytes
 * A chunk the codec doesn't shrink is sent raw.
 */
#pragma once

#include <switch.h>
#include "gecko.h"

namespace Gecko {

    /// LZ4 finds more repeats in bigger chunks; 32 KiB keeps 4 chunks per stage block (one per core).
    constexpr u32 LZ4_CHUNK_SIZE = 0x8000;

    /// Encodes one chunk into out (at least 9 + len bytes). lz4Table: 4096 entries, only for LZ4.
    /// Returns the encoded size.
    u32 encodeChunk(const u8 *src, u32 len, Codec codec, u8 *out, u16 *lz4Table);

    /// Size of one chunk on the wire before encoding for the given codec.
    constexpr u32 chunkSize(Codec codec) { return codec == CodecLz4 ? LZ4_CHUNK_SIZE : GECKO_BUFFER_SIZE; }

    struct EncodedBlock {
        const u8 *data;   // chunk i at data + i * stride
        u32 stride;
        u32 count;
        const u32 *lens;  // encoded size of each chunk
    };

    /// Encodes src (total bytes, at most GECKO_STAGE_SIZE) as consecutive chunks of chunkSize(codec).
    /// The chunks are spread over the command thread and worker threads on the other cores
    /// (idle while a search dump has the game paused). Only one caller at a time (shared lock).
    EncodedBlock encodeChunks(const u8 *src, u32 total, Codec codec);

    /// How many threads encode chunks (1 when the worker threads couldn't be started).
    int encoderThreads();
}
