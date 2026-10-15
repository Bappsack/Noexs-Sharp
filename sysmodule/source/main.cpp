#include <switch.h>
#include <string.h>
#include <sys/stat.h>
#include <cstdio>
#include <cstdarg>
#include <errno.h>
#include "errors.h"
#include "gecko.h"

extern "C" {
    extern u32 __start__;

    u32 __nx_applet_type = AppletType_None;

    // The socket transfer memory is the biggest heap user; with the tuned socket config below
    // it is ~0.6 MiB instead of ~2 MiB, so the heap can shrink from 4 MiB to 2 MiB.
    #define INNER_HEAP_SIZE 0x00200000
    size_t nx_inner_heap_size = INNER_HEAP_SIZE;
    char   nx_inner_heap[INNER_HEAP_SIZE];

    void __libnx_initheap(void);
    void __appInit(void);
    void __appExit(void);
}

// Socket buffers tuned for this workload: large responses (memory dumps) go out, only small
// commands come in. A bigger send buffer keeps the TCP window full (faster dumps) while the
// tiny receive/UDP buffers cut the transfer memory taken from our heap.
static const SocketInitConfig g_socketConfig = {
    .tcp_tx_buf_size     = 0x20000,
    .tcp_rx_buf_size     = 0x4000,
    .tcp_tx_buf_max_size = 0x40000,
    .tcp_rx_buf_max_size = 0,        // fixed
    .udp_tx_buf_size     = 0x800,
    .udp_rx_buf_size     = 0x800,
    .sb_efficiency       = 2,
    .num_bsd_sessions    = 2,
    .bsd_service_type    = BsdServiceType_Auto,
};

// Create this file on the SD card to keep noexs off USB (e.g. when another homebrew needs usb:ds).
#define USB_DISABLE_FLAG "sdmc:/config/noexs/usb_disabled.flag"

// Short status log (startup, USB state, connections). Kept below LOG_MAX_SIZE.
#define LOG_DIR      "sdmc:/config/noexs"
#define LOG_PATH     LOG_DIR "/log.txt"
#define LOG_MAX_SIZE (64 * 1024)

// USB setup is retried until it succeeds (the USB stack may not be ready at boot), first after
// 3 s, then slower and slower: every attempt sets up the console's USB device stack, which is
// shared with controllers and the dock, so retrying quickly forever slowed the whole system.
#define USB_RETRY_FIRST_NS (3000000000ULL)
#define USB_RETRY_MAX_NS (120000000000ULL)
// After this many failed reads in a row, usb:ds is torn down and set up again.
#define USB_MAX_READ_FAILURES (20)

#define USB_THREAD_STACK_SIZE 0x8000

// Keeps an attached game running between client commands (see pumpDebugEvents).
#define DEBUG_THREAD_STACK_SIZE 0x4000
#define DEBUG_WAIT_NS (100000000ULL)

static Gecko::Shared g_Shared;
static FILE* g_debugFile = NULL;

// The last log lines stay in memory so the client can read them over the network (command 0x15),
// also when the SD card log can't be written.
#define LOG_RING_SIZE GECKO_BUFFER_SIZE
static char g_logRing[LOG_RING_SIZE];
static u32 g_logRingPos = 0;
static bool g_logRingFull = false;
static Mutex g_logLock;
static Thread g_usbThread;
static Thread g_debugThread;
static bool g_usbEnabled = false;
static bool g_sdMounted = false;

void __libnx_initheap(void) {
    void*  addr = nx_inner_heap;
    size_t size = nx_inner_heap_size;

    /* Newlib */
    extern char* fake_heap_start;
    extern char* fake_heap_end;

    fake_heap_start = (char*)addr;
    fake_heap_end   = (char*)addr + size;
}

static int openLog();
static void noexsLog(const char* fmt, ...);
static bool g_socketsUp = false;

/*
 * Only sm is essential. Everything else is logged and, where possible, worked around, so a
 * problem shows up in sdmc:/config/noexs/log.txt instead of as a crash at boot.
 */
void __appInit(void) {
    Result rc;
    rc = smInitialize();
    if (R_FAILED(rc)) {
        fatalThrow(MAKERESULT(Module_Libnx, LibnxError_InitFail_SM));
    }

    // libnx picks the command IDs of many services by firmware version. Homebrew apps get it from
    // the loader and libnx' default __appInit reads it, but this custom __appInit has to set it.
    // Without it libnx assumed firmware 0.0.0 and sent pre-5.0/pre-11.0 command IDs: usb:ds
    // closed the session (USB never worked, rc 0xF601), and pmdmntGetApplicationProcessId became
    // pm:dmnt command 5, which on 5.0+ is HookToCreateApplicationProcess: asking for the running
    // game hooked the next game launch.
    Result fwRc = setsysInitialize();
    SetSysFirmwareVersion fw = {};
    if (R_SUCCEEDED(fwRc)) {
        fwRc = setsysGetFirmwareVersion(&fw);
        if (R_SUCCEEDED(fwRc)) {
            hosversionSet(MAKEHOSVERSION(fw.major, fw.minor, fw.micro));
        }
        setsysExit();
    }

    // SD card first, so every later step can be logged
    mutexInit(&g_logLock);
    Result fsRc = fsInitialize();
    Result sdRc = 0;
    if (R_SUCCEEDED(fsRc)) {
        sdRc = fsdevMountSdmc();
        g_sdMounted = R_SUCCEEDED(sdRc);
    }
    int logErr = openLog();
    noexsLog("noexs %d.%d.%d starting (fs rc=0x%x, sd mount rc=0x%x, log file %s, errno %d)", VER_MAJOR, VER_MINOR, VER_PATCH,
        fsRc, sdRc, g_debugFile ? "open" : "NOT open", logErr);
    noexsLog("firmware %u.%u.%u (set:sys rc=0x%x)", HOSVER_MAJOR(hosversionGet()), HOSVER_MINOR(hosversionGet()),
        HOSVER_MICRO(hosversionGet()), fwRc);

    rc = pmdmntInitialize();
    noexsLog("init pm:dmnt rc=0x%x", rc);

    rc = pminfoInitialize();
    noexsLog("init pm:info rc=0x%x", rc);

    rc = socketInitialize(&g_socketConfig);
    noexsLog("init sockets (tuned) rc=0x%x", rc);
    if (R_FAILED(rc)) {
        // the tuned buffer sizes were rejected: fall back to libnx' defaults
        rc = socketInitialize(socketGetDefaultInitConfig());
        noexsLog("init sockets (default) rc=0x%x", rc);
    }
    g_socketsUp = R_SUCCEEDED(rc);
    if (!g_socketsUp) {
        noexsLog("network unavailable, USB only");
    }
}

void __appExit(void) {
    /* Cleanup services. */
    nsExit(); // no-op if a title name was never asked for
    if (g_usbEnabled) {
        Gecko::UsbTransport::exit();
    }
    if (g_socketsUp) {
        socketExit();
    }
    pminfoExit();
    pmdmntExit();
    if (g_sdMounted) {
        fsdevUnmountAll();
    }
    fsExit();
    smExit();
}

void logString(const char* str, int len) {
    mutexLock(&g_logLock);
    for (int i = 0; i < len; i++) {
        g_logRing[g_logRingPos++] = str[i];
        if (g_logRingPos == LOG_RING_SIZE) {
            g_logRingPos = 0;
            g_logRingFull = true;
        }
    }
    if (g_debugFile && len > 0) {
        if (ftell(g_debugFile) > LOG_MAX_SIZE) {
            g_debugFile = freopen(LOG_PATH, "w", g_debugFile); // start over instead of growing
        }
        if (g_debugFile) {
            fwrite(str, 1, len, g_debugFile);
            fflush(g_debugFile);
        }
    }
    mutexUnlock(&g_logLock);
}

u32 copyRecentLog(char* out, u32 max) {
    mutexLock(&g_logLock);
    u32 len = 0;
    if (g_logRingFull) {
        for (u32 i = g_logRingPos; i < LOG_RING_SIZE && len < max; i++) {
            out[len++] = g_logRing[i];
        }
    }
    for (u32 i = 0; i < g_logRingPos && len < max; i++) {
        out[len++] = g_logRing[i];
    }
    mutexUnlock(&g_logLock);
    return len;
}

/// Appends one line to the in-memory log and sdmc:/config/noexs/log.txt (if the SD card works).
static void noexsLog(const char* fmt, ...) {
    char line[256];
    // seconds since boot, so slow steps are visible
    u64 ms = armTicksToNs(armGetSystemTick()) / 1000000ULL;
    int pre = snprintf(line, sizeof(line), "[%5lu.%03lu] ", (unsigned long)(ms / 1000), (unsigned long)(ms % 1000));
    va_list args;
    va_start(args, fmt);
    int len = vsnprintf(line + pre, sizeof(line) - 2 - pre, fmt, args);
    va_end(args);
    if (len < 0) {
        return;
    }
    len += pre;
    if (len > (int)sizeof(line) - 3) {
        len = sizeof(line) - 3;
    }
    line[len++] = '\r';
    line[len++] = '\n';
    logString(line, len);
}

/// Opens the SD card log; returns 0 or the errno of the failing step.
static int openLog() {
    if (!g_sdMounted) {
        return 0;
    }
    mkdir("sdmc:/config", 0777);
    mkdir(LOG_DIR, 0777);
    struct stat st;
    bool tooBig = stat(LOG_PATH, &st) == 0 && st.st_size > LOG_MAX_SIZE;
    g_debugFile = fopen(LOG_PATH, tooBig ? "w" : "a");
    return g_debugFile ? 0 : errno;
}

static Result _eventCallback(Gecko::DebugEvent& event) {
#ifndef NOEXS_LOG_DEBUG_EVENTS
    // debug events are only interesting while developing (and would flood the log)
    (void)event;
    return 0;
#endif
    switch (event.event_type) {
        case 0:
        {
            Gecko::ProcessAttachData pad = event.data.proc_attach;
            printf("ProcessAttachEvent(title_id:%08lx, pid:%08lx, name:'%s', mmu_flags:%d)\r\n", pad.title_id, pad.pid, pad.name, pad.mmu_flags);
        }
        break;
        case 1:
        {
            Gecko::ThreadAttachData tad = event.data.thread_attach;
            printf("ThreadAttachEvent(thread_id:%08lx, tls_pointer:%08lx, entry_point:%08lx)\r\n", tad.thread_id, tad.tls_pointer, tad.entry_point);
        }
        break;
        default:
        printf("UnknownEvent(type:%d Data:", event.event_type);
        for (u32 i = 0; i < DEBUG_DATA_SIZE; i++) {
            printf("%02X", event.data.raw[i]);
        }
        printf(")\r\n");
    }
    return 0;
}

static void sessionBegin(Gecko::Shared& shared) {
    mutexLock(&shared.lock);
    if (shared.activeSessions++ == 0) {
        shared.status = Gecko::Status::Running;
    }
    mutexUnlock(&shared.lock);
}

static void sessionEnd(Gecko::Shared& shared) {
    mutexLock(&shared.lock);
    if (--shared.activeSessions == 0) {
        // last client gone: release the game, like the original reset()
        shared.dbg.detatch();
        shared.status = Gecko::Status::Stopping;
    }
    mutexUnlock(&shared.lock);
}

/// Reads pending debug events; call with g_Shared.lock held. A debugged game stops at every
/// debug event (threads starting/ending, ...) until it is continued. The original only drained
/// events after a client command and never continued, so the game stalled until the next
/// command plus Resume: the game (and with it the console) felt very slow while connected.
static void pumpDebugEvents() {
    Gecko::Status status = g_Shared.status;
    int r = g_Shared.dbg.pump(status != Gecko::Status::Paused);
    if (r & Gecko::PumpExited) {
        noexsLog("debug: attached process exited, detaching");
        g_Shared.dbg.detatch();
        g_Shared.status = Gecko::Status::Running;
    } else if ((r & Gecko::PumpException) && status != Gecko::Status::Paused) {
        // breakpoint, watchpoint or crash: leave it stopped and show it as paused
        g_Shared.status = Gecko::Status::Paused;
    }
}

/// Waits for debug events of the attached game and handles them without waiting for a client.
static void debugThreadMain(void*) {
    while (!g_Shared.exit) {
        mutexLock(&g_Shared.lock);
        Handle handle = g_Shared.dbg.debugHandle();
        u32 gen = g_Shared.dbg.generation();
        mutexUnlock(&g_Shared.lock);
        if (!handle) {
            svcSleepThread(DEBUG_WAIT_NS);
            continue;
        }
        s32 index;
        Result rc = svcWaitSynchronization(&index, &handle, 1, DEBUG_WAIT_NS);
        if (R_FAILED(rc)) {
            if (R_VALUE(rc) != KERNELRESULT(TimedOut)) {
                svcSleepThread(DEBUG_WAIT_NS); // handle closed by a detach: don't spin
            }
            continue;
        }
        mutexLock(&g_Shared.lock);
        if (g_Shared.dbg.generation() == gen) { // still the same attach
            pumpDebugEvents();
        }
        mutexUnlock(&g_Shared.lock);
    }
}

static void startDebugThread() {
    Result rc = threadCreate(&g_debugThread, debugThreadMain, NULL, NULL, DEBUG_THREAD_STACK_SIZE, 44, 3);
    if (R_SUCCEEDED(rc)) {
        rc = threadStart(&g_debugThread);
    }
    if (R_FAILED(rc)) {
        noexsLog("debug: could not start event thread (rc=0x%x)", rc);
    }
}

/// Serves commands from one client until it disconnects or the transport fails.
static void serveSession(Gecko::Transport& conn, int firstCmd) {
    Gecko::Context ctx = { &conn, &g_Shared, false };
    conn.setDoubleBuffering(false); // per-connection options start from the defaults
    sessionBegin(g_Shared);
    int cmd = firstCmd;
    while (!g_Shared.exit && !ctx.stop && cmd >= 0) {
        mutexLock(&g_Shared.lock);
        Result rc = cmd_decode(ctx, cmd);
        pumpDebugEvents();
        mutexUnlock(&g_Shared.lock);
        // after the disconnect command, stop before reading on: over USB nothing closes, so the read
        // would take the next client's first command (its status request) and drop it with this session
        if (rc == MAKERESULT(Module_TCPGecko, TCPGeckoError_iofail) || !conn.connected() || ctx.stop) {
            break;
        }
        cmd = conn.read(); // blocks (with our output already flushed)
    }
    sessionEnd(g_Shared);
}

static Result setupUsb() {
    Result rc = Gecko::UsbTransport::initialize();
    g_usbEnabled = R_SUCCEEDED(rc);
    return rc;
}

static void usbThreadMain(void*) {
    Gecko::UsbTransport usb;
    u64 retryNs = USB_RETRY_FIRST_NS;
    unsigned attempt = 0;
    noexsLog("usb: thread started");
    while (!g_Shared.exit) {
        if (!g_usbEnabled) {
            attempt++;
            u64 start = armGetSystemTick();
            Result rc = setupUsb();
            u64 ms = armTicksToNs(armGetSystemTick() - start) / 1000000ULL;
            if (R_FAILED(rc)) {
                // module/description of the result code help to find the cause (2140 = usb)
                noexsLog("usb: setup attempt %u failed (rc=0x%x = %04d-%04d, took %lu ms), next try in %lus", attempt, rc,
                    2000 + R_MODULE(rc), R_DESCRIPTION(rc), (unsigned long)ms, (unsigned long)(retryNs / 1000000000ULL));
                svcSleepThread(retryNs);
                retryNs = retryNs * 2 > USB_RETRY_MAX_NS ? USB_RETRY_MAX_NS : retryNs * 2;
                continue;
            }
            noexsLog("usb: ready as 057E:3000 after %u attempt(s), %lu ms (waiting for the PC)", attempt, (unsigned long)ms);
            retryNs = USB_RETRY_FIRST_NS;
        }

        int failures = 0;
        while (!g_Shared.exit && g_usbEnabled) {
            usb.disconnect(); // fresh buffers for the next session
            int cmd = usb.read(); // waits for the host to send a command
            if (cmd < 0) {
                UsbState state = UsbState_Detached;
                if (R_SUCCEEDED(usbDsGetState(&state)) && state != UsbState_Configured) {
                    static UsbState lastState = (UsbState)-1;
                    if (state != lastState) {
                        noexsLog("usb: no PC (usb state %d)", (int)state);
                        lastState = state;
                    }
                    // no PC (cable unplugged, charger, dock): just wait, a re-init wouldn't help
                    failures = 0;
                    svcSleepThread(1000000000ULL);
                    continue;
                }
                // configured but failing: back off, and re-initialize if it keeps failing
                if (++failures >= USB_MAX_READ_FAILURES) {
                    // something else may have taken the USB device over: start from scratch
                    noexsLog("usb: %d failed reads, re-initializing", failures);
                    Gecko::UsbTransport::exit();
                    g_usbEnabled = false;
                    break;
                }
                svcSleepThread(500000000ULL);
                continue;
            }
            failures = 0;
            noexsLog("usb: client connected");
            serveSession(usb, cmd);
            noexsLog("usb: client disconnected");
        }
    }
}

static void startUsb() {
    struct stat st;
    if (g_sdMounted && stat(USB_DISABLE_FLAG, &st) == 0) {
        noexsLog("usb: disabled by %s", USB_DISABLE_FLAG);
        return;
    }
    // setup happens (and is retried) on the USB thread so a slow USB stack can't delay Wi-Fi
    // same core as the main thread (the npdm only allows core 3)
    Result rc = threadCreate(&g_usbThread, usbThreadMain, NULL, NULL, USB_THREAD_STACK_SIZE, 49, 3);
    if (R_SUCCEEDED(rc)) {
        rc = threadStart(&g_usbThread);
    }
    if (R_FAILED(rc)) {
        noexsLog("usb: could not start thread (rc=0x%x)", rc);
    }
}

int main(int argc, char **argv) {
    noexsLog("noexs %d.%d.%d running", VER_MAJOR, VER_MINOR, VER_PATCH);
    mutexInit(&g_Shared.lock);
    g_Shared.status = Gecko::Status::Stopping;
    g_Shared.activeSessions = 0;
    g_Shared.exit = false;
    g_Shared.dbg.addEventCallback(_eventCallback);

    startDebugThread();
    startUsb();

    if (!g_socketsUp) {
        // nothing to do on this thread; USB keeps working
        while (appletMainLoop() && !g_Shared.exit) {
            svcSleepThread(1000000000ULL);
        }
    }

    Gecko::TcpTransport tcp;
    noexsLog("tcp: listening on port %d", DEFAULT_LISTEN_PORT);
    int lastLoggedError = 0;
    while (appletMainLoop() && !g_Shared.exit) {
        if (!tcp.accept()) {
            // log each new kind of failure once (not every retry)
            if (tcp.lastError() != lastLoggedError) {
                lastLoggedError = tcp.lastError();
                noexsLog("tcp: %s failed (errno %d)%s", tcp.lastErrorCall(), lastLoggedError,
                    lastLoggedError == EADDRINUSE ? ": port in use, is another noexs (e.g. an old install under another title ID) running?" : "");
            }
            continue;
        }
        lastLoggedError = 0;
        noexsLog("tcp: client connected");
        serveSession(tcp, tcp.read());
        tcp.disconnect();
        noexsLog("tcp: client disconnected");
    }

    g_Shared.exit = true;
    if (g_debugFile) {
        fclose(g_debugFile);
        g_debugFile = NULL;
    }
    mutexLock(&g_Shared.lock);
    g_Shared.dbg.detatch();
    mutexUnlock(&g_Shared.lock);
    return 0;
}
