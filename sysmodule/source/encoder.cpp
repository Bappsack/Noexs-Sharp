#include <string.h>
#include <atomic>
#include "gecko.h"
#include "encoder.h"

using namespace Gecko;

static_assert(GECKO_STAGE_SIZE % GECKO_BUFFER_SIZE == 0, "stage must hold whole RLE chunks");
static_assert(GECKO_STAGE_SIZE % LZ4_CHUNK_SIZE == 0, "stage must hold whole LZ4 chunks");

// Worker threads on cores 0-2 (the command thread itself encodes on core 3). The game's cores are
// idle while a search dump has it paused; the affinity mask lets a worker move to whichever core
// is free otherwise.
#define WORKER_COUNT (3)
#define WORKER_STACK_SIZE (0x4000)
#define WORKER_PRIORITY (49)

#define LZ4_HASH_BITS (12)

static constexpr u32 strideFor(u32 chunk) { return chunk + 0x40; }
static constexpr u32 encodedSize(u32 chunk) { return GECKO_STAGE_SIZE / chunk * strideFor(chunk); }
static constexpr u32 ENCODED_SIZE = encodedSize(GECKO_BUFFER_SIZE) > encodedSize(LZ4_CHUNK_SIZE)
                                        ? encodedSize(GECKO_BUFFER_SIZE) : encodedSize(LZ4_CHUNK_SIZE);

alignas(0x40) static u8 g_encoded[ENCODED_SIZE];
static u32 g_lens[GECKO_STAGE_SIZE / GECKO_BUFFER_SIZE];
// one LZ4 hash table per thread (index WORKER_COUNT: the command thread)
static u16 g_lz4Tables[WORKER_COUNT + 1][1 << LZ4_HASH_BITS];

static Thread g_workers[WORKER_COUNT];
static int g_workerCount = -1; // -1: not started yet
static Semaphore g_work;
static Semaphore g_done;

// the current batch; written before the workers are woken (the semaphore orders the accesses)
static const u8 *g_src;
static u32 g_total;
static u32 g_count;
static u32 g_chunk;
static u32 g_stride;
static Codec g_codec;
static std::atomic<u32> g_next;

static inline u64 load64(const u8 *p) {
    u64 v;
    memcpy(&v, p, sizeof(v));
    return v;
}

static inline u32 load32(const u8 *p) {
    u32 v;
    memcpy(&v, p, sizeof(v));
    return v;
}

static inline u32 lowestSetByte(u64 v) {
#ifdef _MSC_VER
    unsigned long bit;
    _BitScanForward64(&bit, v);
    return (u32)bit >> 3;
#else
    return (u32)__builtin_ctzll(v) >> 3;
#endif
}

/// Length of the run of `value` starting at p[0] (which is value), at most max bytes.
/// Compares 8 bytes at a time: a differing byte is found from the lowest set byte of the xor.
static inline u32 runLength(const u8 *p, u32 max, u8 value) {
    const u64 pattern = 0x0101010101010101ULL * value;
    u32 run = 1;
    while (run + 8 <= max) {
        u64 diff = load64(p + run) ^ pattern;
        if (diff) {
            return run + lowestSetByte(diff);
        }
        run += 8;
    }
    while (run < max && p[run] == value) {
        run++;
    }
    return run;
}

/// RLE into out; returns the encoded size, or 0 if it wouldn't be smaller than len.
static u32 rleEncode(const u8 *src, u32 len, u8 *out) {
    u32 pos = 0;
    u32 i = 0;
    while (i < len) {
        // give up early on data that doesn't shrink (after a quarter of the chunk still
        // ~1.5x the size: almost certainly random data)
        if (pos + 2 > len || (i >= len / 4 && pos > i + i / 2)) {
            return 0;
        }
        u8 value = src[i];
        u32 run = runLength(src + i, len - i < 255 ? len - i : 255, value);
        out[pos++] = value;
        out[pos++] = (u8)run;
        i += run;
    }
    return pos;
}

// ---- LZ4 block format (https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md) ----
// Greedy single-pass compressor with a 4096-entry hash table, like LZ4's "fast" mode.

#define LZ4_MIN_MATCH     (4)
#define LZ4_LAST_LITERALS (5)  // the last 5 bytes are always literals
#define LZ4_MF_LIMIT      (12) // a match must start at least 12 bytes before the end

static inline u32 lz4Hash(u32 v) {
    return (v * 2654435761U) >> (32 - LZ4_HASH_BITS);
}

static inline u8 *lz4Length(u8 *op, u32 rest) {
    while (rest >= 255) {
        *op++ = 255;
        rest -= 255;
    }
    *op++ = (u8)rest;
    return op;
}

/// Room a sequence with lit literals and a match of matchLen bytes needs at most.
static inline u32 lz4SequenceBound(u32 lit, u32 matchLen) {
    return 1 + lit / 255 + 1 + lit + 2 + matchLen / 255 + 1;
}

/// Length of the match at src[ip] / src[ref] (at least LZ4_MIN_MATCH bytes are known to match),
/// not reaching past end.
static inline u32 lz4MatchLength(const u8 *src, u32 ip, u32 ref, u32 end) {
    u32 mlen = LZ4_MIN_MATCH;
    while (ip + mlen + 8 <= end) {
        u64 diff = load64(src + ip + mlen) ^ load64(src + ref + mlen);
        if (diff) {
            return mlen + lowestSetByte(diff);
        }
        mlen += 8;
    }
    while (ip + mlen < end && src[ip + mlen] == src[ref + mlen]) {
        mlen++;
    }
    return mlen;
}

/// LZ4 into out (cap bytes); returns the block size, or 0 if it doesn't fit into cap.
static u32 lz4Encode(const u8 *src, u32 len, u8 *out, u32 cap, u16 *table) {
    u8 *op = out;
    u8 *const oend = out + cap;
    u32 anchor = 0;
    if (len > LZ4_MF_LIMIT) {
        memset(table, 0, sizeof(u16) << LZ4_HASH_BITS);
        const u32 matchEnd = len - LZ4_LAST_LITERALS; // a match may not reach into the last literals
        const u32 lastStart = len - LZ4_MF_LIMIT;     // last position a match may start at
        u32 ip = 1;
        u32 misses = 0;
        while (ip <= lastStart) {
            u32 seq = load32(src + ip);
            u32 h = lz4Hash(seq);
            u32 ref = table[h];
            table[h] = (u16)ip;
            if (ref >= ip || load32(src + ref) != seq) {
                // skip faster through data without matches (LZ4's acceleration)
                ip += 1 + (misses++ >> 6);
                continue;
            }
            misses = 0;
            // extend backwards over literals not yet emitted
            while (ip > anchor && ref > 0 && src[ip - 1] == src[ref - 1]) {
                ip--;
                ref--;
            }
            u32 mlen = lz4MatchLength(src, ip, ref, matchEnd);
            u32 lit = ip - anchor;
            if ((u32)(oend - op) < lz4SequenceBound(lit, mlen)) {
                return 0;
            }
            u32 ml = mlen - LZ4_MIN_MATCH;
            u8 *token = op++;
            *token = (u8)(((lit < 15 ? lit : 15) << 4) | (ml < 15 ? ml : 15));
            if (lit >= 15) {
                op = lz4Length(op, lit - 15);
            }
            memcpy(op, src + anchor, lit);
            op += lit;
            u32 offset = ip - ref; // < 64 KiB: chunks are at most LZ4_CHUNK_SIZE
            *op++ = (u8)offset;
            *op++ = (u8)(offset >> 8);
            if (ml >= 15) {
                op = lz4Length(op, ml - 15);
            }
            ip += mlen;
            anchor = ip;
            if (ip <= lastStart) {
                table[lz4Hash(load32(src + ip - 2))] = (u16)(ip - 2);
            }
        }
    }
    // last literals
    u32 lit = len - anchor;
    if ((u32)(oend - op) < 1 + lit / 255 + 1 + lit) {
        return 0;
    }
    u8 *token = op++;
    *token = (u8)((lit < 15 ? lit : 15) << 4);
    if (lit >= 15) {
        op = lz4Length(op, lit - 15);
    }
    memcpy(op, src + anchor, lit);
    op += lit;
    return (u32)(op - out);
}

u32 Gecko::encodeChunk(const u8 *src, u32 len, Codec codec, u8 *out, u16 *lz4Table) {
    static_assert(LZ4_CHUNK_SIZE <= 0x10000, "LZ4 offsets and the u16 hash table need chunks <= 64 KiB");
    const u32 header = 1 + 4 + 4;
    u32 encoded = 0;
    if (codec == CodecRle) {
        encoded = rleEncode(src, len, out + header);
    } else if (codec == CodecLz4 && len > 4) {
        // only worth it if smaller than the raw chunk (5 + len)
        encoded = lz4Encode(src, len, out + header, len - 4, lz4Table);
    }
    if (encoded > 0) {
        out[0] = (u8)codec;
        memcpy(out + 1, &len, 4);
        memcpy(out + 5, &encoded, 4);
        return header + encoded;
    }
    out[0] = CodecRaw;
    memcpy(out + 1, &len, 4);
    memcpy(out + 5, src, len);
    return 5 + len;
}

static void runBatch(u16 *table) {
    u32 i;
    while ((i = g_next.fetch_add(1, std::memory_order_relaxed)) < g_count) {
        u32 off = i * g_chunk;
        u32 len = g_total - off < g_chunk ? g_total - off : g_chunk;
        g_lens[i] = encodeChunk(g_src + off, len, g_codec, g_encoded + i * g_stride, table);
    }
}

static void workerMain(void *arg) {
    u16 *table = g_lz4Tables[(uintptr_t)arg];
    while (true) {
        semaphoreWait(&g_work);
        runBatch(table);
        semaphoreSignal(&g_done);
    }
}

static void startWorkers() {
    semaphoreInit(&g_work, 0);
    semaphoreInit(&g_done, 0);
    g_workerCount = 0;
    for (int core = 0; core < WORKER_COUNT; core++) {
        Thread *t = &g_workers[g_workerCount];
        if (R_FAILED(threadCreate(t, workerMain, (void *)(uintptr_t)g_workerCount, NULL, WORKER_STACK_SIZE,
                                  WORKER_PRIORITY, core))) {
            continue;
        }
        svcSetThreadCoreMask(t->handle, core, 0xF); // may run on any free core
        if (R_FAILED(threadStart(t))) {
            threadClose(t);
            continue;
        }
        g_workerCount++;
    }
    noexsLog("encoder: %d worker threads (+ command thread)", g_workerCount);
}

int Gecko::encoderThreads() {
    return (g_workerCount < 0 ? 0 : g_workerCount) + 1;
}

EncodedBlock Gecko::encodeChunks(const u8 *src, u32 total, Codec codec) {
    if (g_workerCount < 0) {
        startWorkers();
    }
    g_src = src;
    g_total = total;
    g_codec = codec;
    g_chunk = chunkSize(codec);
    g_stride = strideFor(g_chunk);
    g_count = (total + g_chunk - 1) / g_chunk;
    g_next.store(0, std::memory_order_relaxed);
    // small reads (memory viewer) aren't worth waking anyone
    int helpers = g_count > 1 ? g_workerCount : 0;
    if (helpers > (int)g_count - 1) {
        helpers = (int)g_count - 1;
    }
    for (int i = 0; i < helpers; i++) {
        semaphoreSignal(&g_work);
    }
    runBatch(g_lz4Tables[WORKER_COUNT]);
    for (int i = 0; i < helpers; i++) {
        semaphoreWait(&g_done);
    }
    return EncodedBlock{g_encoded, g_stride, g_count, g_lens};
}
