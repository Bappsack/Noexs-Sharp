/**
 * @file transport.h
 * @brief Buffered byte-stream transports (TCP and USB) for the Gecko protocol.
 *
 * All protocol traffic goes through an output buffer that is flushed in large blocks:
 * explicitly at the end of each command, and implicitly whenever we are about to block
 * waiting for input. Inputs are read through a buffer as well, so a command and its
 * parameters usually cost a single recv()/USB transfer instead of one per field.
 */
#pragma once

#include <switch.h>
#include <string.h>

// noexs 2.x port (0x4E58, "NX"); differs from the original 7331 so old Apps/sysmodules never meet
#define DEFAULT_LISTEN_PORT (20056)

namespace Gecko {

    class Transport {
    public:
        virtual ~Transport() {}

        virtual bool connected() = 0;
        virtual void disconnect() = 0;

        /// Reads exactly len bytes. Returns 0 on success, < 0 on error/closed.
        int read(void *buffer, u32 len);

        /// Reads a single command byte. Returns the byte, or -1 on error/closed.
        int read();

        template <typename T>
        int read(T *to) { return read(to, sizeof(T)); }

        /// Buffered write. Returns 0 on success, < 0 on error.
        int write(const void *buffer, u32 len);

        template <typename T>
        int write(T from) { return write(&from, sizeof(T)); }

        /// Returns a pointer to at least len free bytes in the output buffer (flushing first if
        /// needed), or NULL on error. Data written there becomes part of the stream on commit().
        u8 *reserve(u32 len);
        void commit(u32 len) { outLen += len; }

        /// Sends everything buffered so far. Returns 0 on success, < 0 on error.
        int flush();

        /// Drops buffered data (new session).
        void resetBuffers() { inPos = inLen = 0; outLen = 0; }

    protected:
        Transport(u8 *inBuf, u32 inCap, u8 *outBuf, u32 outCap)
            : inBuf(inBuf), inCap(inCap), inPos(0), inLen(0), outBuf(outBuf), outCap(outCap), outLen(0) {}

        /// Receives up to len bytes (at least 1). Returns the byte count, or <= 0 on error/closed.
        virtual int rawRead(void *buffer, u32 len) = 0;
        /// Sends all len bytes. Returns 0 on success, < 0 on error.
        virtual int rawWrite(const void *buffer, u32 len) = 0;

    private:
        int fill();

        u8 *inBuf;
        u32 inCap, inPos, inLen;
        u8 *outBuf;
        u32 outCap, outLen;
    };

    /// TCP server transport: listens on DEFAULT_LISTEN_PORT and serves one client at a time.
    class TcpTransport : public Transport {
    public:
        TcpTransport();
        ~TcpTransport();
        /// Blocks until a client connects. Returns false (after a short back-off) on failure.
        bool accept(int port = DEFAULT_LISTEN_PORT);
        bool connected() override;
        /// Why the last accept() failed: 0, or the errno of socket/bind/listen/accept.
        int lastError() const { return error; }
        /// Which call failed last: "socket", "bind", "listen" or "accept".
        const char *lastErrorCall() const { return errorCall; }
        void disconnect() override;

    protected:
        int rawRead(void *buffer, u32 len) override;
        int rawWrite(const void *buffer, u32 len) override;

    private:
        int sockfd;
        int error;
        const char *errorCall;
    };

    /// USB transport over libnx usbComms (vendor-specific interface, bulk IN/OUT).
    class UsbTransport : public Transport {
    public:
        UsbTransport();
        /// Sets up usb:ds with the IDs the PC client looks for (057E:3000).
        static Result initialize();
        static void exit();
        bool connected() override { return !broken; }
        void disconnect() override;

    protected:
        int rawRead(void *buffer, u32 len) override;
        int rawWrite(const void *buffer, u32 len) override;

    private:
        bool broken;
    };
}
