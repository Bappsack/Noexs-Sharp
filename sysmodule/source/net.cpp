#include <arpa/inet.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <unistd.h>
#include <errno.h>

#include "transport.h"

using namespace Gecko;

#define DISCONNECTED_FD (-1)

// Commands are tiny; responses (memory dumps) are large, so the output side gets the big buffer.
#define TCP_IN_SIZE  (0x2000)
#define TCP_OUT_SIZE (0x10000)

static u8 g_tcpIn[TCP_IN_SIZE];
static u8 g_tcpOut[TCP_OUT_SIZE];
static u8 g_tcpOut2[TCP_OUT_SIZE]; // second buffer for double buffering

static void kill(int &fd) {
    if (fd != DISCONNECTED_FD) {
        shutdown(fd, SHUT_RDWR);
        close(fd);
        fd = DISCONNECTED_FD;
    }
}

TcpTransport::TcpTransport() : Transport(g_tcpIn, TCP_IN_SIZE, g_tcpOut, g_tcpOut2, TCP_OUT_SIZE), sockfd(DISCONNECTED_FD),
    error(0), errorCall("") {}

TcpTransport::~TcpTransport() {
    stopSender();
    disconnect();
}

bool TcpTransport::accept(int port) {
    disconnect();
    resetBuffers();

    // Errors here used to fatalThrow (crashing the console); now we just back off and retry,
    // e.g. while the network stack isn't up yet.
    error = 0;
    int listenfd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listenfd < 0) {
        error = errno;
        errorCall = "socket";
        svcSleepThread(1000000000ULL);
        return false;
    }

    int yes = 1;
    setsockopt(listenfd, SOL_SOCKET, SO_REUSEADDR, &yes, sizeof(yes));

    struct sockaddr_in addr = {};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = INADDR_ANY;
    addr.sin_port = htons(port);

    if (bind(listenfd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
        error = errno; // EADDRINUSE: another program (e.g. a second noexs) owns the port
        errorCall = "bind";
        kill(listenfd);
        svcSleepThread(1000000000ULL);
        return false;
    }
    if (listen(listenfd, 1) < 0) {
        error = errno;
        errorCall = "listen";
        kill(listenfd);
        svcSleepThread(1000000000ULL);
        return false;
    }

    socklen_t len = sizeof(addr);
    int clientfd = ::accept(listenfd, (struct sockaddr *)&addr, &len);
    kill(listenfd);
    if (clientfd < 0) {
        error = errno;
        errorCall = "accept";
        // e.g. after sleep or a Wi-Fi drop accept fails at once; without this pause the
        // caller rebuilt the socket in a tight loop, keeping core 3 and the bsd service busy
        svcSleepThread(1000000000ULL);
        return false;
    }

    // Small writes are coalesced by our own buffer, so Nagle only adds latency.
    setsockopt(clientfd, IPPROTO_TCP, TCP_NODELAY, &yes, sizeof(yes));
    // Notice dead peers (cable pulled, PC crashed) instead of blocking forever.
    setsockopt(clientfd, SOL_SOCKET, SO_KEEPALIVE, &yes, sizeof(yes));

    sockfd = clientfd;
    return true;
}

bool TcpTransport::connected() {
    return sockfd != DISCONNECTED_FD;
}

void TcpTransport::disconnect() {
    kill(sockfd);
}

int TcpTransport::rawRead(void *buffer, u32 len) {
    if (sockfd == DISCONNECTED_FD) {
        return -1;
    }
    int ret = recv(sockfd, buffer, len, 0);
    if (ret <= 0) {
        return -1; // 0 = peer closed
    }
    return ret;
}

int TcpTransport::rawWrite(const void *buffer, u32 len) {
    const u8 *ptr = (const u8 *)buffer;
    while (len > 0) {
        if (sockfd == DISCONNECTED_FD) {
            return -1;
        }
        int ret = send(sockfd, ptr, len, 0);
        if (ret <= 0) {
            return -1;
        }
        len -= ret;
        ptr += ret;
    }
    return 0;
}
