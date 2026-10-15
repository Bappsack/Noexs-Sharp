#include <string.h>
#include <atomic>
#include "gecko.h"
#include "encoder.h"

using namespace Gecko;

#define CHUNKS_PER_STAGE (GECKO_STAGE_SIZE / GECKO_BUFFER_SIZE)
static_assert(GECKO_STAGE_SIZE % GECKO_BUFFER_SIZE == 0, "stage must hold whole chunks");
static_assert(ENCODED_CHUNK_STRIDE >= 9 + GECKO_BUFFER_SIZE, "encoded chunk doesn't fit its slot");

// Worker threads on cores 0-2 (the command thread itself encodes on core 3). The game's cores are
// idle while a search dump has it paused; the affinity mask lets a worker move to whichever core
// is free otherwise.
#define WORKER_COUNT (3)
#define WORKER_STACK_SIZE (0x4000)
#define WORKER_PRIORITY (49)

alignas(0x40) static u8 g_encoded[CHUNKS_PER_STAGE * ENCODED_CHUNK_STRIDE];
static u32 g_lens[CHUNKS_PER_STAGE];

static Thread g_workers[WORKER_COUNT];
static int g_workerCount = -1; // -1: not started yet
static Semaphore g_work;
static Semaphore g_done;

// the current batch; written before the workers are woken (the semaphore orders the accesses)
static const u8 *g_src;
static u32 g_total;
static u32 g_count;
static bool g_compress;
static std::atomic<u32> g_next;

static inline u64 load64(const u8 *p) {
    u64 v;
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

u32 Gecko::encodeChunk(const u8 *src, u32 len, bool compress, u8 *out) {
    if (compress) {
        const u32 header = 1 + 4 + 4;
        u8 *rle = out + header;
        u32 pos = 0;
        u32 i = 0;
        while (i < len) {
            // RLE must end up smaller than the data; give up early on data that doesn't shrink
            // (after a quarter of the chunk still ~1.5x the size: almost certainly random data)
            if (pos + 2 > len || (i >= len / 4 && pos > i + i / 2)) {
                break;
            }
            u8 value = src[i];
            u32 run = runLength(src + i, len - i < 255 ? len - i : 255, value);
            rle[pos++] = value;
            rle[pos++] = (u8)run;
            i += run;
        }
        if (i == len) {
            out[0] = 1;
            memcpy(out + 1, &len, 4);
            memcpy(out + 5, &pos, 4);
            return header + pos;
        }
    }
    out[0] = 0;
    memcpy(out + 1, &len, 4);
    memcpy(out + 5, src, len);
    return 5 + len;
}

static void runBatch() {
    u32 i;
    while ((i = g_next.fetch_add(1, std::memory_order_relaxed)) < g_count) {
        u32 off = i * GECKO_BUFFER_SIZE;
        u32 len = g_total - off < GECKO_BUFFER_SIZE ? g_total - off : GECKO_BUFFER_SIZE;
        g_lens[i] = encodeChunk(g_src + off, len, g_compress, g_encoded + i * ENCODED_CHUNK_STRIDE);
    }
}

static void workerMain(void *) {
    while (true) {
        semaphoreWait(&g_work);
        runBatch();
        semaphoreSignal(&g_done);
    }
}

static void startWorkers() {
    semaphoreInit(&g_work, 0);
    semaphoreInit(&g_done, 0);
    g_workerCount = 0;
    for (int core = 0; core < WORKER_COUNT; core++) {
        Thread *t = &g_workers[g_workerCount];
        if (R_FAILED(threadCreate(t, workerMain, NULL, NULL, WORKER_STACK_SIZE, WORKER_PRIORITY, core))) {
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

const u8 *Gecko::encodeChunks(const u8 *src, u32 total, bool compress, const u32 **lens) {
    if (g_workerCount < 0) {
        startWorkers();
    }
    g_src = src;
    g_total = total;
    g_count = (total + GECKO_BUFFER_SIZE - 1) / GECKO_BUFFER_SIZE;
    g_compress = compress;
    g_next.store(0, std::memory_order_relaxed);
    // small reads (memory viewer) aren't worth waking anyone
    int helpers = g_count > 1 ? g_workerCount : 0;
    if (helpers > (int)g_count - 1) {
        helpers = (int)g_count - 1;
    }
    for (int i = 0; i < helpers; i++) {
        semaphoreSignal(&g_work);
    }
    runBatch();
    for (int i = 0; i < helpers; i++) {
        semaphoreWait(&g_done);
    }
    *lens = g_lens;
    return g_encoded;
}
