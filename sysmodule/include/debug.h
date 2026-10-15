#pragma once

#include <switch.h>
#include <functional>
#include <list>

#define DEBUG_DATA_SIZE (0x30)

extern "C"{
    Result svcSetHardwareBreakpoint(u32 id, u64 flags, u64 value);
}

namespace Gecko{
    
    #pragma pack(push, 1)
    struct ProcessAttachData{
        u64 title_id;
        u64 pid;
        char name[12];
        u32 mmu_flags;
    };
    
    struct ThreadAttachData{
        u64 thread_id;
        u64 tls_pointer;
        u64 entry_point;
    };
    
    struct ExitData{
        u64 type;
    };
    
    struct ExceptionData{
        u64 type;
        u64 fault_reg;
        u64 per_exception;
    };
    
    union DebugData{
        u8 raw[DEBUG_DATA_SIZE];
        ProcessAttachData proc_attach;
        ThreadAttachData thread_attach;
        //unk2
        ExitData exit;
        ExceptionData exception;
    };
    
    struct DebugEvent{
        u32 event_type;
        u32 flags;
        u64 thread_id;
        DebugData data;
    };
    #pragma pack(pop)
    
    /// What pump() saw (bit flags).
    enum PumpResult{
        PumpEvents    = 1, // at least one event was read
        PumpException = 2, // the process stopped on an exception (breakpoint, crash, break)
        PumpExited    = 4, // the process ended
    };

    class Debugger{
        u64 pid;
        u32 gen; // changes on every attach/detach
        std::list<std::function<Result(Gecko::DebugEvent&)>>callbacks;
        Result fireEvent(Gecko::DebugEvent& event);
        Handle handle;
        public:
        Debugger() :  pid(0), gen(0), handle(0) { }
        Result flushEvents();
        /// Reads all pending debug events. While the game should be running, it is continued
        /// right away: a debuggee stays frozen after every event (e.g. a thread starting)
        /// until it is continued. Exceptions are left stopped for the client to inspect.
        int pump(bool keepRunning);
        Handle debugHandle() { return handle; }
        u32 generation() { return gen; }
        void addEventCallback(std::function<Result(Gecko::DebugEvent&)> callback);
        u64 attachedPid();
        Result attach(u64 pid);
        Result attached();
        Result detatch();
        
        Result resume();
        Result pause();
        
        Result query(MemoryInfo* to, u64 addr);
        
        Result listPids(u64* pids, s32* count, u32 max);
        
        Result readMem(void *buffer, u64 addr, u64 size);
        template <typename T>
        Result readMem(T* to, u64 addr){ return readMem(to, addr, sizeof(T)); }
        
        Result writeMem(void *buffer, u64 addr, u64 size);
        template <typename T>
        Result writeMem(T from, u64 addr) { return writeMem(&from, addr, sizeof(T)); }
        
        Result setBreakpoint(u32 id, u64 flags, u64 addr);
        ~Debugger();
    };
}
