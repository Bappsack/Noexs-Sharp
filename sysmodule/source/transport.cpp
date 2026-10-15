#include "transport.h"

using namespace Gecko;

#define SENDER_STACK_SIZE (0x4000)
// above the command thread (49): a finished buffer is put on the wire right away
#define SENDER_PRIORITY (44)

Transport::Transport(u8 *inBuf, u32 inCap, u8 *outBuf, u8 *outBuf2, u32 outCap)
    : inBuf(inBuf), inCap(inCap), inPos(0), inLen(0), outBuf(outBuf), outCap(outCap), outLen(0),
      asyncOn(false), threadUp(false), quit(false), jobBuf(NULL), jobLen(0), jobResult(0),
      stall(0), sendTicks(0), sentBytes(0) {
    outBufs[0] = outBuf;
    outBufs[1] = outBuf2;
    semaphoreInit(&work, 0);
    semaphoreInit(&idle, 1);
}

int Transport::sendNow(const u8 *buffer, u32 len) {
    u64 start = armGetSystemTick();
    int rc = rawWrite(buffer, len);
    sendTicks.fetch_add(armGetSystemTick() - start, std::memory_order_relaxed);
    sentBytes.fetch_add((u64)len, std::memory_order_relaxed);
    return rc;
}

void Transport::senderMain(void *arg) {
    Transport *t = (Transport *)arg;
    while (true) {
        semaphoreWait(&t->work);
        if (t->quit) {
            break;
        }
        t->jobResult = t->sendNow(t->jobBuf, t->jobLen);
        semaphoreSignal(&t->idle);
    }
}

bool Transport::setDoubleBuffering(bool on) {
    if (on && !threadUp) {
        if (!outBufs[1]) {
            return asyncOn = false;
        }
        Result rc = threadCreate(&sender, senderMain, this, NULL, SENDER_STACK_SIZE, SENDER_PRIORITY, -2);
        if (R_SUCCEEDED(rc)) {
            rc = threadStart(&sender);
            if (R_FAILED(rc)) {
                threadClose(&sender);
            }
        }
        threadUp = R_SUCCEEDED(rc);
        if (!threadUp) {
            return asyncOn = false;
        }
    }
    if (!on) {
        drain(); // nothing may still be in flight from the other buffer
    }
    return asyncOn = on;
}

void Transport::stopSender() {
    if (!threadUp) {
        return;
    }
    drain();
    quit = true;
    semaphoreSignal(&work);
    threadWaitForExit(&sender);
    threadClose(&sender);
    threadUp = false;
    asyncOn = false;
}

int Transport::drain() {
    if (!threadUp) {
        return 0;
    }
    u64 start = armGetSystemTick();
    semaphoreWait(&idle);
    stall.fetch_add(armGetSystemTick() - start, std::memory_order_relaxed);
    int rc = jobResult;
    semaphoreSignal(&idle); // still idle
    return rc < 0 ? -1 : 0;
}

void Transport::resetBuffers() {
    drain();
    jobResult = 0;
    inPos = inLen = 0;
    outLen = 0;
}

Transport::Stats Transport::takeStats() {
    Stats s;
    s.stallNs = armTicksToNs(stall.exchange(0, std::memory_order_relaxed));
    s.sendNs = armTicksToNs(sendTicks.exchange(0, std::memory_order_relaxed));
    s.bytes = sentBytes.exchange(0, std::memory_order_relaxed);
    return s;
}

int Transport::fill() {
    // about to block for input: the peer may be waiting for what we buffered
    if (outLen > 0 && flush() < 0) {
        return -1;
    }
    if (drain() < 0) {
        return -1;
    }
    int n = rawRead(inBuf, inCap);
    if (n <= 0) {
        inPos = inLen = 0;
        return -1;
    }
    inPos = 0;
    inLen = (u32)n;
    return 0;
}

int Transport::read(void *buffer, u32 len) {
    u8 *dst = (u8 *)buffer;
    while (len > 0) {
        if (inPos == inLen && fill() < 0) {
            return -1;
        }
        u32 n = inLen - inPos;
        if (n > len) {
            n = len;
        }
        memcpy(dst, inBuf + inPos, n);
        inPos += n;
        dst += n;
        len -= n;
    }
    return 0;
}

int Transport::read() {
    u8 b;
    if (read(&b, 1) < 0) {
        return -1;
    }
    return b;
}

int Transport::write(const void *buffer, u32 len) {
    const u8 *src = (const u8 *)buffer;
    if (outCap - outLen < len && flush() < 0) {
        return -1;
    }
    if (len > outCap) {
        // too big to buffer: send directly, after anything still in flight
        if (drain() < 0) {
            return -1;
        }
        return sendNow(src, len);
    }
    memcpy(outBuf + outLen, src, len);
    outLen += len;
    return 0;
}

u8 *Transport::reserve(u32 len) {
    if (len > outCap) {
        return NULL;
    }
    if (outCap - outLen < len && flush() < 0) {
        return NULL;
    }
    return outBuf + outLen;
}

int Transport::flush() {
    if (outLen == 0) {
        return 0;
    }
    u32 len = outLen;
    outLen = 0;
    if (!asyncOn) {
        u64 start = armGetSystemTick();
        int rc = sendNow(outBuf, len);
        stall.fetch_add(armGetSystemTick() - start, std::memory_order_relaxed);
        return rc;
    }
    // wait until the sender has finished the other buffer, hand it this one, continue in the other
    u64 start = armGetSystemTick();
    semaphoreWait(&idle);
    stall.fetch_add(armGetSystemTick() - start, std::memory_order_relaxed);
    if (jobResult < 0) {
        semaphoreSignal(&idle);
        return -1;
    }
    jobBuf = outBuf;
    jobLen = len;
    semaphoreSignal(&work);
    outBuf = outBuf == outBufs[0] ? outBufs[1] : outBufs[0];
    return 0;
}
