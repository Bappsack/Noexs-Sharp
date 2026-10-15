#include "debug.h"
#include "errors.h"
#include <string.h>


#define RETURN_NOT_ATTACHED() {       \
    Result rc = attached();         \
    if(R_FAILED(rc)){               \
        return rc;                  \
    }                               \
}

void Gecko::Debugger::addEventCallback(std::function<Result(Gecko::DebugEvent&)> callback){
    callbacks.push_back(callback);
}

Result Gecko::Debugger::fireEvent(Gecko::DebugEvent& event){
    Result rc = 0;
    std::list<std::function<Result(Gecko::DebugEvent&)>>::iterator it;
    for (it=callbacks.begin(); it!=callbacks.end() && R_SUCCEEDED(rc); ++it) {
        rc = (*it)(event);
    }
    return rc;
}

Result Gecko::Debugger::flushEvents(){
    RETURN_NOT_ATTACHED();
    Result rc = 0;
    static_assert(sizeof(Gecko::DebugEvent) <= sizeof(DebugEventInfo), "debug event layout");
    do{
        DebugEventInfo info;
        rc = svcGetDebugEvent(&info, handle);
        if(R_SUCCEEDED(rc)){
            Gecko::DebugEvent event;
            memcpy(&event, &info, sizeof(event));
            fireEvent(event);
        }
    }while(R_SUCCEEDED(rc));
    return rc;
}

int Gecko::Debugger::pump(bool keepRunning){
    if(!handle){
        return 0;
    }
    int result = 0;
    DebugEventInfo info;
    while(R_SUCCEEDED(svcGetDebugEvent(&info, handle))){
        Gecko::DebugEvent event;
        memcpy(&event, &info, sizeof(event));
        fireEvent(event);
        result |= PumpEvents;
        if(event.event_type == 2){        // ExitProcess
            result |= PumpExited;
        }else if(event.event_type == 4){  // Exception
            result |= PumpException;
        }
    }
    if((result & PumpEvents) && keepRunning && !(result & (PumpException | PumpExited))){
        svcContinueDebugEvent(handle, 4 | 2 | 1, 0, 0);
    }
    return result;
}

u64 Gecko::Debugger::attachedPid(){
    return pid;
}

Result Gecko::Debugger::attach(u64 pid){
    Result rc = attached();
    if(R_SUCCEEDED(rc)){
        return MAKERESULT(Module_TCPGecko, TCPGeckoError_already_attached);
    }
    rc = svcDebugActiveProcess(&handle, pid);
    if(R_SUCCEEDED(rc)){
        this->pid = pid;
        gen++;
    }
    return rc;
}

Result Gecko::Debugger::attached(){
    if(!handle){
        return MAKERESULT(Module_TCPGecko, TCPGeckoError_not_attached);
    }
    return 0;
}

Result Gecko::Debugger::detatch(){
    RETURN_NOT_ATTACHED();
    Result rc = svcCloseHandle(handle);
    if(R_SUCCEEDED(rc)){
        pid = 0;
        handle = 0;
        gen++;
    }
    return rc;
}

Result Gecko::Debugger::resume(){
    RETURN_NOT_ATTACHED();
    flushEvents();
    return svcContinueDebugEvent(handle, 4 | 2 | 1, 0, 0); //TODO: TID
}

Result Gecko::Debugger::pause(){
    RETURN_NOT_ATTACHED();
    return svcBreakDebugProcess(handle);
}

Result Gecko::Debugger::query(MemoryInfo* to, u64 addr){
    RETURN_NOT_ATTACHED();
    u32 pageinfo; // ignored
    return svcQueryDebugProcessMemory(to, &pageinfo, handle, addr);
}

Result Gecko::Debugger::listPids(u64* pids, s32* count, u32 max){
    return svcGetProcessList(count, pids, max);
}

Result Gecko::Debugger::readMem(void *buffer, u64 addr, u64 size){
    RETURN_NOT_ATTACHED();
    return svcReadDebugProcessMemory(buffer, handle, addr, size);
}

Result Gecko::Debugger::writeMem(void *buffer, u64 addr, u64 size){
    RETURN_NOT_ATTACHED();
    return svcWriteDebugProcessMemory(handle, buffer, addr, size);
}

Result Gecko::Debugger::setBreakpoint(u32 id, u64 flag, u64 value){
    if(value == 0){
        value = handle;
    }
    return svcSetHardwareBreakpoint(id, flag, value);
}

Gecko::Debugger::~Debugger(){
    Result rc = attached();
    if(R_SUCCEEDED(rc)){
        rc = detatch();
    }
}
