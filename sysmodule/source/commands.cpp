#include <stdlib.h>
#include <string.h>
#include "gecko.h"
#include "errors.h"

#define IOFAIL MAKERESULT(Module_TCPGecko, TCPGeckoError_iofail)

//useful macros for reading/writing to the stream
#define READ_CHECKED(ctx, to) {                                     \
    if (ctx.conn->read(&to) < 0)                                    \
        return IOFAIL;                                              \
}

#define READ_BUFFER_CHECKED(ctx, buffer, size) {                    \
    if (ctx.conn->read(buffer, size) < 0)                           \
        return IOFAIL;                                              \
}

#define WRITE_CHECKED(ctx, value) {                                 \
    if (ctx.conn->write(value) < 0)                                 \
        return IOFAIL;                                              \
}

#define WRITE_BUFFER_CHECKED(ctx, buffer, size) {                   \
    if (ctx.conn->write(buffer, size) < 0)                          \
        return IOFAIL;                                              \
}

#define DBG (ctx.shared->dbg)

/*
 * Chunk format (unchanged): u8 compressed, u32 len, [u32 compressedLen], data.
 * RLE data is a list of (value, count) byte pairs. The chunk is compressed directly into the
 * transport's output buffer; if RLE doesn't pay off it is sent raw instead.
 */
static Result writeCompressed(Gecko::Context &ctx, const u8 *src, u32 len) {
    const u32 header = 1 + 4 + 4;
    u8 *out = ctx.conn->reserve(header + len * 2);
    if (!out) {
        return IOFAIL;
    }
    u8 *rle = out + header;
    u32 pos = 0;
    u32 i = 0;
    while (i < len && pos <= len) {
        u8 value = src[i];
        u32 max = len - i < 255 ? len - i : 255;
        u32 run = 1;
        while (run < max && src[i + run] == value) {
            run++;
        }
        rle[pos++] = value;
        rle[pos++] = (u8)run;
        i += run;
    }

    if (i == len && pos <= len) {
        out[0] = 1;
        memcpy(out + 1, &len, 4);
        memcpy(out + 5, &pos, 4);
        ctx.conn->commit(header + pos);
    } else {
        out[0] = 0;
        memcpy(out + 1, &len, 4);
        memcpy(out + 5, src, len);
        ctx.conn->commit(5 + len);
    }
    return 0;
}

//0x01
static Result _status(Gecko::Context &ctx) {
    WRITE_CHECKED(ctx, (u8)ctx.shared->status);
    WRITE_CHECKED(ctx, (u8)VER_MAJOR);
    WRITE_CHECKED(ctx, (u8)VER_MINOR);
    WRITE_CHECKED(ctx, (u8)VER_PATCH);
    return 0;
}

//0x02
static Result _poke8(Gecko::Context &ctx) {
    u64 ptr;
    u8 val;
    READ_CHECKED(ctx, ptr);
    READ_CHECKED(ctx, val);
    return DBG.writeMem(val, ptr);
}

//0x03
static Result _poke16(Gecko::Context &ctx) {
    u64 ptr;
    u16 val;
    READ_CHECKED(ctx, ptr);
    READ_CHECKED(ctx, val);
    return DBG.writeMem(val, ptr);
}

//0x04
static Result _poke32(Gecko::Context &ctx) {
    u64 ptr;
    u32 val;
    READ_CHECKED(ctx, ptr);
    READ_CHECKED(ctx, val);
    return DBG.writeMem(val, ptr);
}

//0x05
static Result _poke64(Gecko::Context &ctx) {
    u64 ptr;
    u64 val;
    READ_CHECKED(ctx, ptr);
    READ_CHECKED(ctx, val);
    return DBG.writeMem(val, ptr);
}

//0x06
static Result _readmem(Gecko::Context &ctx) {
    Result rc;
    u64 addr;
    u32 size;

    READ_CHECKED(ctx, addr);
    READ_CHECKED(ctx, size);
    rc = DBG.attached();
    WRITE_CHECKED(ctx, rc);
    if (R_FAILED(rc)) {
        return rc;
    }
    u8 *stage = ctx.shared->stage;
    while (size > 0) {
        // one svc call per 64 KiB block; if the block fails (e.g. it touches an unmapped page)
        // fall back to per-chunk reads so the reported error position matches the old behaviour
        u32 block = size < GECKO_STAGE_SIZE ? size : GECKO_STAGE_SIZE;
        bool blockOk = R_SUCCEEDED(DBG.readMem(stage, addr, block));
        for (u32 off = 0; off < block; off += GECKO_BUFFER_SIZE) {
            u32 len = block - off < GECKO_BUFFER_SIZE ? block - off : GECKO_BUFFER_SIZE;
            u8 *src = stage + off;
            rc = blockOk ? 0 : DBG.readMem(src, addr + off, len);
            WRITE_CHECKED(ctx, rc);
            if (R_FAILED(rc)) {
                return rc;
            }
            rc = writeCompressed(ctx, src, len);
            if (R_FAILED(rc)) {
                return rc;
            }
        }
        addr += block;
        size -= block;
    }
    return rc;
}

//0x07
static Result _writemem(Gecko::Context &ctx) {
    u64 addr;
    u32 size;
    u32 len;
    Result rc = 0;
    READ_CHECKED(ctx, addr);
    READ_CHECKED(ctx, size);
    rc = DBG.attached();
    WRITE_CHECKED(ctx, rc); // flushed automatically before we wait for the data
    if (R_SUCCEEDED(rc)) {
        u8 *buffer = ctx.shared->buffer;
        while (size > 0) {
            len = size < GECKO_BUFFER_SIZE ? size : GECKO_BUFFER_SIZE;
            READ_BUFFER_CHECKED(ctx, buffer, len);
            DBG.writeMem(buffer, addr, len);
            addr += len;
            size -= len;
        }
    }
    return 0;
}

//0x08
static Result _resume(Gecko::Context &ctx) {
    Result rc = DBG.resume();
    if (R_SUCCEEDED(rc)) {
        ctx.shared->status = Gecko::Status::Running;
    }
    return rc;
}

//0x09
static Result _pause(Gecko::Context &ctx) {
    Result rc = DBG.pause();
    if (R_SUCCEEDED(rc)) {
        ctx.shared->status = Gecko::Status::Paused;
    }
    return rc;
}

//0x0A
static Result _attach(Gecko::Context &ctx) {
    u64 pid;
    READ_CHECKED(ctx, pid);
    Result rc = DBG.attach(pid);
    if (R_SUCCEEDED(rc)) {
        ctx.shared->status = Gecko::Status::Paused;
    }
    return rc;
}

//0x0B
static Result _detatch(Gecko::Context &ctx) {
    Result rc = DBG.detatch();
    if (R_SUCCEEDED(rc)) {
        ctx.shared->status = Gecko::Status::Running;
    }
    return rc;
}

static Result writeInfo(Gecko::Context &ctx, const MemoryInfo &info) {
    WRITE_CHECKED(ctx, info.addr);
    WRITE_CHECKED(ctx, info.size);
    WRITE_CHECKED(ctx, info.type);
    WRITE_CHECKED(ctx, info.perm);
    return 0;
}

//0x0C
static Result _querymem_single(Gecko::Context &ctx) {
    u64 addr;
    MemoryInfo info = {};
    READ_CHECKED(ctx, addr);
    Result rc = DBG.query(&info, addr);
    Result wrc = writeInfo(ctx, info);
    return R_FAILED(wrc) ? wrc : rc;
}

//0x0D
static Result _querymem_multi(Gecko::Context &ctx) {
    Result rc = 0;
    u64 addr;
    u32 requestCount;
    MemoryInfo info = {};
    READ_CHECKED(ctx, addr);
    READ_CHECKED(ctx, requestCount);

    for (u32 count = 0; count < requestCount; count++) {
        rc = DBG.query(&info, addr);
        Result wrc = writeInfo(ctx, info);
        if (R_FAILED(wrc)) {
            return wrc;
        }
        WRITE_CHECKED(ctx, rc);
        if (info.type == 0x10 || R_FAILED(rc)) {
            break;
        }
        addr += info.size;
    }
    return rc;
}

//0x0E
static Result _current_pid(Gecko::Context &ctx) {
    u64 pid = 0;
    Result rc = pmdmntGetApplicationProcessId(&pid);
    WRITE_CHECKED(ctx, pid);
    return rc;
}

//0x0F
static Result _attached_pid(Gecko::Context &ctx) {
    WRITE_CHECKED(ctx, DBG.attachedPid());
    return 0;
}

//0x10
static Result _list_pids(Gecko::Context &ctx) {
    Result rc;
    int maxpids = GECKO_BUFFER_SIZE / sizeof(u64);
    s32 count = 0;
    rc = DBG.listPids((u64 *)ctx.shared->buffer, &count, maxpids);
    if (R_FAILED(rc)) {
        count = 0;
    }
    WRITE_CHECKED(ctx, count);
    WRITE_BUFFER_CHECKED(ctx, ctx.shared->buffer, count * sizeof(u64));
    return rc;
}

//0x11
static Result _get_titleid(Gecko::Context &ctx) {
    Result rc;
    u64 pid;
    u64 title_id;

    READ_CHECKED(ctx, pid);

    rc = pminfoGetProgramId(&title_id, pid);
    if (R_FAILED(rc)) {
        title_id = 0;
    }
    WRITE_CHECKED(ctx, title_id);
    return rc;
}

//0x12
static Result _disconnect(Gecko::Context &ctx) {
    ctx.stop = true;
    return 0;
}

//0x13
static Result _readmem_multi(Gecko::Context &ctx) {
    Result rc = 0;
    u32 size;
    u32 data_size;
    u64 addr;
    READ_CHECKED(ctx, size);
    READ_CHECKED(ctx, data_size);
    rc = DBG.attached();
    if (R_SUCCEEDED(rc) && data_size > GECKO_BUFFER_SIZE) {
        rc = MAKERESULT(Module_TCPGecko, TCPGeckoError_buffer_too_small);
    }
    WRITE_CHECKED(ctx, rc);
    if (R_SUCCEEDED(rc)) {
        u8 *buffer = ctx.shared->buffer;
        for (u32 count = 0; count < size; count++) {
            READ_CHECKED(ctx, addr);
            rc = DBG.readMem(buffer, addr, data_size);
            WRITE_CHECKED(ctx, rc);
            if (R_FAILED(rc)) {
                break;
            }
            WRITE_BUFFER_CHECKED(ctx, buffer, data_size);
        }
    }
    return rc;
}

//0x14
static Result _set_breakpoint(Gecko::Context &ctx) {
    u32 id;
    u64 addr;
    u64 flags;
    READ_CHECKED(ctx, id);
    READ_CHECKED(ctx, addr);
    READ_CHECKED(ctx, flags);
    return DBG.setBreakpoint(id, flags, addr);
}

//0x15: the sysmodule's recent log (u32 length + text), readable without the SD card
static Result _get_log(Gecko::Context &ctx) {
    u32 len = copyRecentLog((char *)ctx.shared->buffer, sizeof(ctx.shared->buffer));
    WRITE_CHECKED(ctx, len);
    WRITE_BUFFER_CHECKED(ctx, ctx.shared->buffer, len);
    return 0;
}

/// The name of an installed application in the console's language (from its control data / NACP).
static Result getApplicationName(u64 titleId, char *out, size_t max) {
    out[0] = 0;
    static bool nsUp = false;
    if (!nsUp) {
        Result rc = nsInitialize(); // only when a client asks, so ns isn't touched at boot
        if (R_FAILED(rc)) {
            return rc;
        }
        nsUp = true;
    }
    // ~144 KiB (NACP + icon); allocated only for the call
    NsApplicationControlData *data = (NsApplicationControlData *)malloc(sizeof(NsApplicationControlData));
    if (!data) {
        return MAKERESULT(Module_Libnx, LibnxError_OutOfMemory);
    }
    u64 size = 0;
    Result rc = nsGetApplicationControlData(NsApplicationControlSource_Storage, titleId, data, sizeof(*data), &size);
    if (R_SUCCEEDED(rc) && size >= sizeof(NacpStruct)) {
        NacpLanguageEntry *entry = NULL;
        if (R_FAILED(nacpGetLanguageEntry(&data->nacp, &entry)) || !entry || !entry->name[0]) {
            entry = NULL;
            for (int i = 0; i < 16 && !entry; i++) { // console language missing: any language
                if (data->nacp.lang[i].name[0]) {
                    entry = &data->nacp.lang[i];
                }
            }
        }
        if (entry) {
            strncpy(out, entry->name, max - 1);
            out[max - 1] = 0;
        }
    } else if (R_SUCCEEDED(rc)) {
        rc = MAKERESULT(Module_Libnx, LibnxError_BadInput);
    }
    free(data);
    return rc;
}

//0x16: name of an installed game (in: u64 title id; out: u32 length + UTF-8 name)
static Result _get_title_name(Gecko::Context &ctx) {
    u64 titleId;
    READ_CHECKED(ctx, titleId);
    char name[0x201];
    Result rc = getApplicationName(titleId, name, sizeof(name));
    u32 len = (u32)strlen(name);
    WRITE_CHECKED(ctx, len);
    WRITE_BUFFER_CHECKED(ctx, name, len);
    return rc;
}

typedef Result (*Command)(Gecko::Context &);

static const Command g_commands[] = {
    NULL, _status, _poke8, _poke16, _poke32, _poke64, _readmem,
    _writemem, _resume, _pause, _attach, _detatch, _querymem_single,
    _querymem_multi, _current_pid, _attached_pid, _list_pids,
    _get_titleid, _disconnect, _readmem_multi, _set_breakpoint, _get_log, _get_title_name,
};

Result cmd_decode(Gecko::Context &ctx, int cmd) {
    Result rc;
    if (cmd > 0 && (u32)cmd < sizeof(g_commands) / sizeof(g_commands[0]) && g_commands[cmd]) {
        rc = g_commands[cmd](ctx);
    } else {
        rc = MAKERESULT(Module_TCPGecko, TCPGeckoError_invalid_cmd);
    }
    WRITE_CHECKED(ctx, rc);
    if (ctx.conn->flush() < 0) {
        return IOFAIL;
    }
    return rc;
}
