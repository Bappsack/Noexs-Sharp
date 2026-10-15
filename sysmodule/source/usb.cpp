#include "transport.h"

using namespace Gecko;

// Page-aligned so usbComms can transfer straight from/to them (no bounce-buffer copies).
// Sizes are multiples of every USB bulk max-packet size (64/512/1024), so a read can never
// overflow on a packet boundary.
#define USB_IN_SIZE  (0x4000)
// 256 KiB per transfer: the per-transfer overhead (posting the buffer, the host picking it up)
// is paid once per 256 KiB instead of once per 64 KiB.
#define USB_OUT_SIZE (0x40000)

alignas(0x1000) static u8 g_usbIn[USB_IN_SIZE];
alignas(0x1000) static u8 g_usbOut[USB_OUT_SIZE];
alignas(0x1000) static u8 g_usbOut2[USB_OUT_SIZE]; // second buffer for double buffering

// Matches the IDs the PC client searches for.
#define USB_VENDOR_ID  (0x057E)
#define USB_PRODUCT_ID (0x3000)

// A bulk transfer ends with a short packet. If a flush were an exact multiple of the packet
// size the host would keep waiting for more data, so such flushes are split so the final
// transfer is short (64 divides every full/high/super-speed bulk packet size).
#ifndef USB_SHORT_PACKET_GRANULARITY
#define USB_SHORT_PACKET_GRANULARITY (64)
#endif

UsbTransport::UsbTransport() : Transport(g_usbIn, USB_IN_SIZE, g_usbOut, g_usbOut2, USB_OUT_SIZE), broken(false) {}

UsbTransport::~UsbTransport() {
    stopSender();
}

Result UsbTransport::initialize() {
    // vendor-specific class: no host driver claims it, the PC client talks to it directly
    UsbCommsInterfaceInfo info = {0xFF, 0xFF, 0xFF};
    return usbCommsInitializeEx(1, &info, USB_VENDOR_ID, USB_PRODUCT_ID);
}

void UsbTransport::exit() {
    usbCommsExit();
}

void UsbTransport::disconnect() {
    // USB has no connection to close: the next command simply starts a new session
    resetBuffers();
    broken = false;
}

int UsbTransport::rawRead(void *buffer, u32 len) {
    // blocks until the host sends something (or the cable/configuration goes away)
    size_t n = usbCommsRead(buffer, len);
    if (n == 0) {
        broken = true;
        return -1;
    }
    return (int)n;
}

static bool writeAll(const u8 *ptr, u32 len) {
    return usbCommsWrite(ptr, len) == len;
}

int UsbTransport::rawWrite(const void *buffer, u32 len) {
    const u8 *ptr = (const u8 *)buffer;
    bool ok;
    if (len % USB_SHORT_PACKET_GRANULARITY == 0) {
        ok = writeAll(ptr, len - 1) && writeAll(ptr + len - 1, 1);
    } else {
        ok = writeAll(ptr, len);
    }
    if (!ok) {
        broken = true;
        return -1;
    }
    return 0;
}
