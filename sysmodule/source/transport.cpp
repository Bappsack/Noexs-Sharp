#include "transport.h"

using namespace Gecko;

int Transport::fill() {
    // about to block for input: the peer may be waiting for what we buffered
    if (outLen > 0 && flush() < 0) {
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
        return rawWrite(src, len); // too big to buffer: send directly (buffer is empty now)
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
    return rawWrite(outBuf, len);
}
