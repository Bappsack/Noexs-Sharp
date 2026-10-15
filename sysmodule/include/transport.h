/**
 * @file transport.h
 * @brief Buffered byte-stream transports (TCP and USB) for the Gecko protocol.
 *
 * All protocol traffic goes through an output buffer that is flushed in large blocks:
 * explicitly at the end of each command, and implicitly whenever we are about to block
 * waiting for input. Inputs are read through a buffer as well, so a command and its
 * parameters usually cost a single recv()/USB transfer instead of one per field.
 *
 * With double buffering on, a sender thread transmits one output buffer while the command
 * fills the other one, so reading and compressing game memory overlaps with the transfer.
 */
#pragma once

#include <switch.h>
#include <string.h>
#include <atomic>

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

        /// Sends everything buffered so far (with double buffering: hands it to the sender thread).
        /// Returns 0 on success, < 0 on error.
        int flush();

        /// Waits until everything handed to the sender thread is sent. Returns 0 or < 0 on error.
        int drain();

        /// Drops buffered data (new session).
        void resetBuffers();

        /// Turns double buffering on/off (starts the sender thread on first use). Returns the new state.
        bool setDoubleBuffering(bool on);
        bool doubleBuffering() const { return asyncOn; }

        /// Time the command thread waited for the link, time spent sending, bytes sent (since the last call).
        struct Stats {
            u64 stallNs;
            u64 sendNs;
            u64 bytes;
        };
        Stats takeStats();
        /// Running stall counter (ticks), to subtract waits from other measurements.
        u64 stallTicks() const { return stall.load(std::memory_order_relaxed); }

    protected:
        /// outBuf2 (same size as outBuf) is only used with double buffering.
        Transport(u8 *inBuf, u32 inCap, u8 *outBuf, u8 *outBuf2, u32 outCap);

        /// Stops the sender thread; derived classes call this in their destructor (rawWrite is virtual).
        void stopSender();

        /// Receives up to len bytes (at least 1). Returns the byte count, or <= 0 on error/closed.
        virtual int rawRead(void *buffer, u32 len) = 0;
        /// Sends all len bytes. Returns 0 on success, < 0 on error.
        virtual int rawWrite(const void *buffer, u32 len) = 0;

    private:
        int fill();
        int sendNow(const u8 *buffer, u32 len); // rawWrite + statistics
        static void senderMain(void *arg);

        u8 *inBuf;
        u32 inCap, inPos, inLen;
        u8 *outBuf;
        u8 *outBufs[2];
        u32 outCap, outLen;

        // double buffering: the sender thread sends jobBuf/jobLen; "idle" has a count of 1 while
        // it has nothing to send, "work" wakes it up
        bool asyncOn;
        bool threadUp;
        volatile bool quit;
        Thread sender;
        Semaphore work;
        Semaphore idle;
        const u8 *volatile jobBuf;
        volatile u32 jobLen;
        volatile int jobResult;

        std::atomic<u64> stall;     // ticks the command thread waited for the link
        std::atomic<u64> sendTicks; // ticks spent in rawWrite
        std::atomic<u64> sentBytes;
    };

    /// TCP server transport: listens on DEFAULT_LISTEN_PORT and serves one client at a time.
    class TcpTransport : public Transport {
    public:
        TcpTransport();
        ~TcpTransport() override;
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
        ~UsbTransport() override;
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
