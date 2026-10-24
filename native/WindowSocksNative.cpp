// WindowSocks routing engine. SOCKS handshakes are linked from the pinned MIT ProxyBridge sources.
// No WinDivert import library or driver is needed to build or run protocol/policy tests.
#include <winsock2.h>
#include <windows.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <sddl.h>
#include <atomic>
#include <algorithm>
#include <array>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <string>
#include <thread>
#include <vector>
#include <chrono>
#include <functional>
#include <tuple>
#include <set>
#include <span>
#include <condition_variable>
#include <cstdio>
extern "C" {
#include "pb_internal.h"
LogCallback g_log_callback = nullptr;
}
#define API extern "C" __declspec(dllexport)

struct RouteRule { uint32_t pid, proxy; uint64_t created; uint32_t blocked, reserved; };
struct Traffic { uint64_t sent, received, dropped; };
enum Stage : uint32_t { Captured=1, Connected=2, ClientEof=3, UpstreamEof=4, Closed=5, PolicyClosed=6, SocketGone=7,
    IdleExpired=8, DialFailed=20, SocksFailed=21, SelectFailed=22, ClientReadFailed=23, UpstreamReadFailed=24,
    SendToProxyFailed=25, SendToClientFailed=26, UdpFailed=27, EngineStopped=28 };
struct FlowEvent {
    uint64_t sequence,flow,at,created,sent,received;
    uint32_t pid,proxy,src,dst,sp,dp,protocol,stage,error,reserved;
};
struct Flow;
static bool gatewayStillBound(const Flow& f);
static void stopGateway();
static void trace(const Flow& f,uint32_t stage,uint32_t error=0);
static std::atomic<uint64_t> nextFlow{0},nextEvent{0};
static std::mutex eventMutex;
static std::deque<FlowEvent> events;
struct Key {
    uint32_t src, dst; uint16_t sp, dp; uint8_t protocol;
    bool operator<(const Key& b) const { return std::tie(src,dst,sp,dp,protocol) < std::tie(b.src,b.dst,b.sp,b.dp,b.protocol); }
};
static bool gatewayDhcpResponse(const Key& key,uint32_t index);
struct Flow {
    uint64_t id=++nextFlow,opened=GetTickCount64();
    Key key{}; RouteRule rule{}; uint16_t alias{}; uint64_t socketCreated{}; WINDIVERT_ADDRESS address{};
    uint32_t gatewayHost=0,gatewayInterface=0;
    std::atomic<SOCKET> client{INVALID_SOCKET}, upstream{INVALID_SOCKET}, datagram{INVALID_SOCKET};
    std::atomic<bool> alive{true}, connected{false}; std::atomic<uint64_t> last{0};
    std::atomic<uint64_t> sent{0},received{0};
    std::mutex queueMutex; std::deque<std::vector<uint8_t>> queue;
    void close(uint32_t reason=Closed,uint32_t error=0) { if(alive.exchange(false))trace(*this,reason,error); connected=false; for (auto* p : {&client,&upstream,&datagram}) { SOCKET s=p->exchange(INVALID_SOCKET); if(s!=INVALID_SOCKET) { shutdown(s,SD_BOTH); closesocket(s); } } }
    ~Flow() { close(); }
};
static void trace(const Flow& f,uint32_t stage,uint32_t error) {
    if((stage==ClientEof||stage==UpstreamEof)&&!f.alive)return; // A local policy/socket close is not an upstream EOF.
    FILETIME now;GetSystemTimeAsFileTime(&now);uint64_t at=((uint64_t)now.dwHighDateTime<<32)|now.dwLowDateTime;
    std::lock_guard lk(eventMutex);
    events.push_back({++nextEvent,f.id,at,f.rule.created,f.sent.load(),f.received.load(),f.rule.pid,f.rule.proxy,
        f.key.src,f.key.dst,f.key.sp,f.key.dp,f.key.protocol,stage,error,0});
    while(events.size()>1024)events.pop_front();
}
static std::shared_mutex stateMutex;
static std::vector<RouteRule> rules;
static std::vector<std::wstring> guards;
// State: 0 disabled, 1 proxy, 2 quarantine. Replaced atomically with process rules.
static uint32_t globalProxy=0,globalState=0;
static bool globalBypassLan=true;
static bool globalBypassRemote=false;
static std::vector<std::wstring> remoteImages;
static uint64_t remoteGeneration=0,rdpCreated=0;
static uint32_t rdpPid=0,rdpPort=0;
static std::mutex remoteCacheMutex;
static std::map<std::pair<uint32_t,uint64_t>,std::pair<uint64_t,bool>> remoteCache;
static std::vector<uint8_t> ownerSid;
// Inherited identities remain attached after their parent exits, until unbinding or process exit.
static std::map<std::pair<uint32_t,uint64_t>,std::pair<uint32_t,uint64_t>> descendants;
// Unrelated browser/background traffic must not take a full process snapshot on every packet.
// New children have a new identity; every policy replacement invalidates these short negative results.
static std::map<std::pair<uint32_t,uint64_t>,uint64_t> unrelatedProcesses;
static std::map<std::pair<uint32_t,uint64_t>,bool> guardResults; // stateMutex
struct ProcessLifetime { HANDLE handle; uint64_t exited; };
static std::map<std::pair<uint32_t,uint64_t>,ProcessLifetime> processLifetimes;
// Job membership is inherited by Windows before a child's first packet, even through a short-lived helper.
// No kill-on-close, resource limits or UI restrictions are applied. Explicit breakaway remains allowed.
static std::map<std::pair<uint32_t,uint64_t>,HANDLE> processJobs;
static std::map<uint32_t, PROXY_CONFIG> profiles;
static std::map<std::pair<uint32_t,uint64_t>, Traffic> traffic;
static std::mutex flowMutex, sendMutex, lifecycleMutex;
static std::map<Key,std::shared_ptr<Flow>> flows;
static std::map<uint16_t,std::shared_ptr<Flow>> aliases;
static std::atomic<bool> active{false};
static std::atomic<unsigned> workers{0};
static std::thread captureThread, acceptThread, pruneThread, retryThread;
static SOCKET listener=INVALID_SOCKET;
static uint16_t listenPort=0, nextAlias=20000;
static HANDLE divert=INVALID_HANDLE_VALUE;
static HMODULE driverLibrary=nullptr;
static decltype(&WinDivertOpen) wdOpen;
static decltype(&WinDivertRecv) wdRecv;
static decltype(&WinDivertRecvEx) wdRecvEx;
static decltype(&WinDivertSend) wdSend;
static decltype(&WinDivertSendEx) wdSendEx;
static decltype(&WinDivertHelperParsePacket) wdParsePacket;
static decltype(&WinDivertClose) wdClose;
static decltype(&WinDivertHelperCalcChecksums) wdChecksums;
static decltype(&WinDivertShutdown) wdShutdown;
static std::atomic<uint32_t> lastError{0};
struct PacketMetrics {
    std::atomic<uint64_t> packets{0},bytes{0},impostor{0},loopback{0},icmp{0},batches{0},ownerQueries{0},ownerUs{0},ownerMaxUs{0},
        classifyUs{0},classifyMaxUs{0},sendUs{0},sendMaxUs{0},ageUs{0},ageMaxUs{0},ageSamples{0},unknown{0},overflow{0},parseErrors{0},
        routeUs{0},routeWaitUs{0},guardUs{0},guardWaitUs{0},ancestryWalks{0},ancestryUs{0},imageUs{0},parentQueries{0},parentErrors{0};
};
static PacketMetrics packetMetrics;
static std::wstring telemetryPath,telemetryDirectory;
static unsigned telemetryPart=0;
static uint64_t qpcUs() { LARGE_INTEGER now,frequency;QueryPerformanceCounter(&now);QueryPerformanceFrequency(&frequency);return (uint64_t)(now.QuadPart/frequency.QuadPart)*1000000+(uint64_t)(now.QuadPart%frequency.QuadPart)*1000000/frequency.QuadPart; }
static void peak(std::atomic<uint64_t>& maximum,uint64_t value){auto old=maximum.load(std::memory_order_relaxed);while(old<value&&!maximum.compare_exchange_weak(old,value,std::memory_order_relaxed)){} }
struct MetricTimer { std::atomic<uint64_t>& total;uint64_t start=qpcUs();~MetricTimer(){total+=qpcUs()-start;} };
static void rotateTelemetry(){
    SYSTEMTIME now;GetSystemTime(&now);wchar_t file[160];swprintf_s(file,L"\\packet-%04u%02u%02u-%02u%02u%02u%03u-%lu-%u.jsonl",now.wYear,now.wMonth,now.wDay,now.wHour,now.wMinute,now.wSecond,now.wMilliseconds,GetCurrentProcessId(),++telemetryPart);
    telemetryPath=telemetryDirectory+file;
    // Retain at most sixteen 8-MiB segments, including the new one. Only our
    // generated packet logs are eligible; no user documents or flow logs.
    std::vector<std::pair<uint64_t,std::wstring>> files;WIN32_FIND_DATAW entry{};
    HANDLE search=FindFirstFileW((telemetryDirectory+L"\\packet-*.jsonl").c_str(),&entry);
    if(search!=INVALID_HANDLE_VALUE){do{if(!(entry.dwFileAttributes&(FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)))files.emplace_back(((uint64_t)entry.ftLastWriteTime.dwHighDateTime<<32)|entry.ftLastWriteTime.dwLowDateTime,entry.cFileName);}while(FindNextFileW(search,&entry));FindClose(search);}
    std::sort(files.begin(),files.end());for(size_t i=0;i+15<files.size();i++)DeleteFileW((telemetryDirectory+L"\\"+files[i].second).c_str());
}
static void startTelemetry(){
    wchar_t image[32768]{};DWORD count=GetModuleFileNameW(nullptr,image,32768);if(!count||count>=32768)return;
    const wchar_t* name=wcsrchr(image,L'\\');if(!name||_wcsicmp(name+1,L"WindowSocks.Service.exe"))return;
    wchar_t root[32768];DWORD length=GetEnvironmentVariableW(L"ProgramData",root,32768);if(!length||length>=32768)return;
    telemetryDirectory=std::wstring(root)+L"\\WindowSocks\\diagnostics";CreateDirectoryW(telemetryDirectory.c_str(),nullptr);rotateTelemetry();
}
static void saveTelemetry(){
    if(telemetryPath.empty())return;
    WIN32_FILE_ATTRIBUTE_DATA info{};if(GetFileAttributesExW(telemetryPath.c_str(),GetFileExInfoStandard,&info)&&((uint64_t)info.nFileSizeHigh<<32|info.nFileSizeLow)>8*1024*1024)rotateTelemetry();
    FILE* file=nullptr;if(_wfopen_s(&file,telemetryPath.c_str(),L"a")||!file)return;
    SYSTEMTIME now;GetSystemTime(&now);
    auto& m=packetMetrics;
    fprintf(file,"{\"utc\":\"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ\",\"native\":\"0.1.23\",\"pid\":%lu,\"packets\":%llu,\"bytes\":%llu,\"batches\":%llu,\"impostor\":%llu,\"loopback\":%llu,\"icmp\":%llu,\"ownerQueries\":%llu,\"ownerUs\":%llu,\"ownerMaxUs\":%llu,\"classifyUs\":%llu,\"classifyMaxUs\":%llu,\"sendUs\":%llu,\"sendMaxUs\":%llu,\"ageUs\":%llu,\"ageMaxUs\":%llu,\"ageSamples\":%llu,\"unknown\":%llu,\"overflow\":%llu,\"parseErrors\":%llu}\n",
        now.wYear,now.wMonth,now.wDay,now.wHour,now.wMinute,now.wSecond,now.wMilliseconds,GetCurrentProcessId(),m.packets.load(),m.bytes.load(),m.batches.load(),m.impostor.load(),m.loopback.load(),m.icmp.load(),m.ownerQueries.load(),m.ownerUs.load(),m.ownerMaxUs.load(),m.classifyUs.load(),m.classifyMaxUs.load(),m.sendUs.load(),m.sendMaxUs.load(),m.ageUs.load(),m.ageMaxUs.load(),m.ageSamples.load(),m.unknown.load(),m.overflow.load(),m.parseErrors.load());
    fprintf(file,"{\"utc\":\"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ\",\"detail\":true,\"routeUs\":%llu,\"routeWaitUs\":%llu,\"guardUs\":%llu,\"guardWaitUs\":%llu,\"ancestryWalks\":%llu,\"ancestryUs\":%llu,\"imageUs\":%llu,\"parentQueries\":%llu,\"parentErrors\":%llu}\n",now.wYear,now.wMonth,now.wDay,now.wHour,now.wMinute,now.wSecond,now.wMilliseconds,m.routeUs.load(),m.routeWaitUs.load(),m.guardUs.load(),m.guardWaitUs.load(),m.ancestryWalks.load(),m.ancestryUs.load(),m.imageUs.load(),m.parentQueries.load(),m.parentErrors.load());
    fclose(file);
}

static uint16_t be16(const uint8_t* p) { return (uint16_t)((p[0]<<8)|p[1]); }
static void put16(uint8_t* p,uint16_t x) { p[0]=(uint8_t)(x>>8);p[1]=(uint8_t)x; }
static uint32_t word(const uint8_t* p) { uint32_t x;memcpy(&x,p,4);return x; }
static uint64_t created(uint32_t pid) {
    if(!pid||pid==UINT32_MAX)return 0;
    // Keep the actual process object, not a timed PID result. Checking its exit time
    // on every use prevents PID reuse while avoiding OpenProcess/CloseHandle per packet.
    struct Handles {
        std::mutex mutex;std::map<uint32_t,HANDLE> items;
        ~Handles(){for(auto& [id,p]:items)CloseHandle(p);}
    };
    static Handles cache;std::lock_guard lk(cache.mutex);
    auto it=cache.items.find(pid);FILETIME c,e,k,u;
    if(it!=cache.items.end()) {
        if(GetProcessTimes(it->second,&c,&e,&k,&u)&&!e.dwHighDateTime&&!e.dwLowDateTime)
            return ((uint64_t)c.dwHighDateTime<<32)|c.dwLowDateTime;
        CloseHandle(it->second);cache.items.erase(it);
    }
    HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(!p)return 0;
    if(!GetProcessTimes(p,&c,&e,&k,&u)||e.dwHighDateTime||e.dwLowDateTime){CloseHandle(p);return 0;}
    if(cache.items.size()>=4096){auto first=cache.items.begin();CloseHandle(first->second);cache.items.erase(first);}
    cache.items[pid]=p;
    return ((uint64_t)c.dwHighDateTime<<32)|c.dwLowDateTime;
}
static void updateProcessJobs() { // stateMutex is held exclusively.
    for(const auto& r:rules)if((r.reserved&1)&&processJobs.find({r.pid,r.created})==processJobs.end()) {
        HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|PROCESS_SET_QUOTA|PROCESS_TERMINATE,FALSE,r.pid);
        if(!process)continue;
        FILETIME c,e,k,u;bool valid=GetProcessTimes(process,&c,&e,&k,&u)&&!e.dwHighDateTime&&!e.dwLowDateTime&&(((uint64_t)c.dwHighDateTime<<32)|c.dwLowDateTime)==r.created;
        bool covered=false;
        if(valid)for(const auto& [root,job]:processJobs) {
            bool followed=std::any_of(rules.begin(),rules.end(),[&](const auto& parent){return parent.pid==root.first&&parent.created==root.second&&(parent.reserved&1);});
            BOOL member=FALSE;if(followed&&IsProcessInJob(process,job,&member)&&member){covered=true;break;}
        }
        if(valid&&!covered) {
            HANDLE job=CreateJobObjectW(nullptr,nullptr);JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
            limits.BasicLimitInformation.LimitFlags=JOB_OBJECT_LIMIT_BREAKAWAY_OK;
            if(job&&SetInformationJobObject(job,JobObjectExtendedLimitInformation,&limits,sizeof(limits))&&AssignProcessToJobObject(job,process))processJobs[{r.pid,r.created}]=job;
            else if(job)CloseHandle(job);
        }
        CloseHandle(process);
    }
    for(auto it=processJobs.begin();it!=processJobs.end();) {
        JOBOBJECT_BASIC_ACCOUNTING_INFORMATION accounting{};
        if(QueryInformationJobObject(it->second,JobObjectBasicAccountingInformation,&accounting,sizeof(accounting),nullptr)&&accounting.ActiveProcesses==0){CloseHandle(it->second);it=processJobs.erase(it);}
        else ++it;
    }
}
static std::vector<std::pair<uint32_t,uint64_t>> jobMembers(uint32_t pid,uint64_t birth) { // stateMutex is held.
    std::vector<std::pair<uint32_t,uint64_t>> result;auto it=processJobs.find({pid,birth});if(it==processJobs.end())return result;
    std::vector<uint8_t> data(sizeof(JOBOBJECT_BASIC_PROCESS_ID_LIST)+sizeof(ULONG_PTR)*1024);
    if(!QueryInformationJobObject(it->second,JobObjectBasicProcessIdList,data.data(),(DWORD)data.size(),nullptr))return result;
    auto* list=reinterpret_cast<JOBOBJECT_BASIC_PROCESS_ID_LIST*>(data.data());
    for(DWORD i=0;i<list->NumberOfProcessIdsInList;i++){uint32_t child=(uint32_t)list->ProcessIdList[i];uint64_t childBirth=created(child);if(child!=pid&&childBirth)result.emplace_back(child,childBirth);}
    return result;
}
static bool guarded(uint32_t pid) {
    MetricTimer timer{packetMetrics.guardUs};uint64_t waiting=qpcUs();
    std::unique_lock lk(stateMutex);packetMetrics.guardWaitUs+=qpcUs()-waiting;if(guards.empty())return false;
    uint64_t birth=created(pid);if(!birth)return false;
    auto key=std::make_pair(pid,birth);auto cached=guardResults.find(key);
    if(cached!=guardResults.end())return cached->second;
    if(guardResults.size()>=4096)guardResults.clear();
    auto result=[&](bool value){guardResults[key]=value;return value;};
    HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(!p)return false;
    if(!ownerSid.empty()) {
        HANDLE token=nullptr; bool owned=false;
        if(OpenProcessToken(p,TOKEN_QUERY,&token)){DWORD bytes=0;GetTokenInformation(token,TokenUser,nullptr,0,&bytes);std::vector<uint8_t> info(bytes);if(bytes&&GetTokenInformation(token,TokenUser,info.data(),bytes,&bytes))owned=EqualSid(((TOKEN_USER*)info.data())->User.Sid,ownerSid.data())!=0;CloseHandle(token);}
        if(!owned){CloseHandle(p);return result(false);}
    }
    wchar_t path[32768]; DWORD n=32768;uint64_t querying=qpcUs();bool ok=QueryFullProcessImageNameW(p,0,path,&n)!=0;packetMetrics.imageUs+=qpcUs()-querying;CloseHandle(p);
    if(!ok)return false;
    const wchar_t* name=wcsrchr(path,L'\\');name=name?name+1:path;
    for(const auto& g:guards) if(_wcsicmp(wcspbrk(g.c_str(),L"\\/")?path:name,g.c_str())==0) return result(true);
    return result(false);
}
// Layout documented by Microsoft for NtQueryInformationProcess(ProcessBasicInformation).
// Query one pinned process object. A whole-system Toolhelp snapshot on the packet
// thread takes hundreds of milliseconds on busy hosts and starves every application.
struct NativeProcessBasicInfo { LONG exitStatus;void* peb;ULONG_PTR affinity;LONG priority;ULONG_PTR pid,parent; };
using QueryProcessInfo=LONG(NTAPI*)(HANDLE,ULONG,void*,ULONG,ULONG*);
static QueryProcessInfo queryProcessInfo(){static auto fn=(QueryProcessInfo)GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"NtQueryInformationProcess");return fn;}
static uint32_t parentOf(uint32_t pid,uint64_t birth) {
    ++packetMetrics.parentQueries;HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
    if(!process){++packetMetrics.parentErrors;return 0;}
    FILETIME c,e,k,u;NativeProcessBasicInfo info{};ULONG length=0;auto query=queryProcessInfo();
    bool valid=GetProcessTimes(process,&c,&e,&k,&u)&&!e.dwLowDateTime&&!e.dwHighDateTime&&(((uint64_t)c.dwHighDateTime<<32)|c.dwLowDateTime)==birth;
    bool ok=valid&&query&&query(process,0,&info,sizeof(info),&length)>=0&&length==sizeof(info)&&info.pid==pid&&info.parent<=UINT32_MAX;
    CloseHandle(process);if(!ok){++packetMetrics.parentErrors;return 0;}return (uint32_t)info.parent;
}
static int route(uint32_t pid,uint64_t birth,bool ipv6,RouteRule& found) {
    if(pid==GetCurrentProcessId())return 0;
    MetricTimer timer{packetMetrics.routeUs};uint64_t waiting=qpcUs();std::unique_lock lk(stateMutex);packetMetrics.routeWaitUs+=qpcUs()-waiting;
    auto action=[&](const RouteRule& r){found=r;found.pid=pid;found.created=birth;return r.blocked==2?3:r.blocked||ipv6||profiles.find(r.proxy)==profiles.end()?2:1;};
    for(const auto& r:rules) if(r.pid==pid && r.created==birth && birth) {
        return action(r);
    }
    if(!birth)return 0;
    if(std::none_of(rules.begin(),rules.end(),[](const auto&r){return (r.reserved&1)!=0;}))return 0;
    // A job proves membership of the currently opened process, not of a stale PID+birth supplied by a caller.
    // Validate before consulting either captured descendants or Windows job membership.
    if(created(pid)!=birth)return 0;
    auto unrelated=unrelatedProcesses.find({pid,birth});
    if(unrelated!=unrelatedProcesses.end()&&GetTickCount64()-unrelated->second<1000)return 0;
    if(!ownerSid.empty()) {
        HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid),token=nullptr;bool owned=false;
        if(p&&OpenProcessToken(p,TOKEN_QUERY,&token)){DWORD bytes=0;GetTokenInformation(token,TokenUser,nullptr,0,&bytes);std::vector<uint8_t> info(bytes);
            if(bytes&&GetTokenInformation(token,TokenUser,info.data(),bytes,&bytes))owned=EqualSid(((TOKEN_USER*)info.data())->User.Sid,ownerSid.data())!=0;CloseHandle(token);}
        if(p)CloseHandle(p);if(!owned)return 0;
    }
    auto inherited=descendants.find({pid,birth});
    if(inherited!=descendants.end())for(const auto& r:rules)if(r.pid==inherited->second.first&&r.created==inherited->second.second&&(r.reserved&1))return action(r);
    HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
    const RouteRule* jobRule=nullptr;bool conflict=false;
    if(process){for(const auto& r:rules)if(r.reserved&1) {
        auto job=processJobs.find({r.pid,r.created});BOOL member=FALSE;
        if(job!=processJobs.end()&&IsProcessInJob(process,job->second,&member)&&member){
            if(jobRule&&(jobRule->proxy!=r.proxy||jobRule->blocked!=r.blocked))conflict=true;
            else jobRule=&r;
        }
    }CloseHandle(process);}
    if(conflict)return 2;
    if(jobRule){if(descendants.size()<4096)descendants[{pid,birth}]={jobRule->pid,jobRule->created};return action(*jobRule);}
    ++packetMetrics.ancestryWalks;MetricTimer ancestryTimer{packetMetrics.ancestryUs};
    uint32_t child=pid;uint64_t childBirth=birth;
    for(int depth=0;depth<32;depth++) {
        uint32_t parent=parentOf(child,childBirth);if(!parent||parent==child)break;
        child=parent;uint64_t parentBirth=created(child);
        if(!parentBirth||parentBirth>childBirth) {
            parentBirth=0;
            for(auto& [identity,lifetime]:processLifetimes)if(identity.first==child&&identity.second<=childBirth) {
                if(lifetime.handle){FILETIME c,e,k,u;if(GetProcessTimes(lifetime.handle,&c,&e,&k,&u))lifetime.exited=((uint64_t)e.dwHighDateTime<<32)|e.dwLowDateTime;
                    if(lifetime.exited){CloseHandle(lifetime.handle);lifetime.handle=nullptr;}}
                if(lifetime.exited&&childBirth<=lifetime.exited)parentBirth=(std::max)(parentBirth,identity.second);
            }
        }
        if(!parentBirth||parentBirth>childBirth)break;childBirth=parentBirth;
        for(const auto& r:rules)if(r.pid==child&&r.created==parentBirth&&(r.reserved&1)){
            if(descendants.size()<4096)descendants[{pid,birth}]={r.pid,r.created};
            return action(r);
        }
    }
    if(unrelatedProcesses.size()>=4096)unrelatedProcesses.clear();
    unrelatedProcesses[{pid,birth}]=GetTickCount64();
    return 0;
}
static void count(uint32_t pid,uint64_t sent,uint64_t received,uint64_t dropped,uint64_t birth=0) {
    if(!birth)birth=created(pid);if(!birth)return;
    std::unique_lock lk(stateMutex); auto& t=traffic[{pid,birth}];t.sent+=sent;t.received+=received;t.dropped+=dropped;
}
static bool remoteControlProcess(uint32_t pid,uint64_t birth) {
    if(!pid||pid==UINT32_MAX||!birth)return false;
    std::vector<std::wstring> images;uint64_t generation;
    {std::shared_lock lk(stateMutex);if(!globalState||!globalBypassRemote||remoteImages.empty())return false;generation=remoteGeneration;}
    const auto key=std::make_pair(pid,birth);
    {std::lock_guard lk(remoteCacheMutex);auto it=remoteCache.find(key);if(it!=remoteCache.end()&&it->second.first==generation)return it->second.second;}
    {std::shared_lock lk(stateMutex);images=remoteImages;generation=remoteGeneration;}
    bool match=false;HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
    if(process) {
        FILETIME c,e,k,u;wchar_t path[32768];DWORD length=32768;
        if(GetProcessTimes(process,&c,&e,&k,&u)&&(((uint64_t)c.dwHighDateTime<<32)|c.dwLowDateTime)==birth&&QueryFullProcessImageNameW(process,0,path,&length)) {
            const wchar_t* name=wcsrchr(path,L'\\');name=name?name+1:path;
            for(const auto& image:images)if(_wcsicmp(wcspbrk(image.c_str(),L"\\/")?path:name,image.c_str())==0){match=true;break;}
        }
        CloseHandle(process);
    }
    {std::shared_lock lk(stateMutex);if(!globalState||!globalBypassRemote||remoteGeneration!=generation)return false;}
    {std::lock_guard lk(remoteCacheMutex);if(remoteCache.size()>=4096)remoteCache.clear();remoteCache[key]={generation,match};}
    return match;
}
static bool remoteRdpListener(uint32_t pid,uint64_t birth,uint16_t localPort) {
    std::shared_lock lk(stateMutex);
    // Restrict svchost/System exceptions to the configured RDP listener, not all service traffic.
    return globalState&&globalBypassRemote&&rdpPid&&rdpPort&&localPort==rdpPort&&
        ((pid==rdpPid&&birth&&birth==rdpCreated)||pid==4);
}
static int policyAction(uint32_t pid,uint64_t birth,bool ipv6,RouteRule& rule,uint16_t localPort=0) {
    if(pid==GetCurrentProcessId())return 0; // Relay, health checks and SOCKS bootstrap must never recurse.
    int action=route(pid,birth,ipv6,rule);
    if(action)return action;
    if(guarded(pid))return 2; // Startup guards and paused bindings override the global fallback.
    if(remoteRdpListener(pid,birth,localPort)||remoteControlProcess(pid,birth)){rule={pid,0,birth,2,0};return 3;}
    std::shared_lock lk(stateMutex);
    if(!globalState)return 0;
    rule={pid,globalProxy,birth,globalState==2?1u:0u,2};
    return !pid||pid==UINT32_MAX||!birth||ipv6||globalState==2||profiles.find(globalProxy)==profiles.end()?2:1;
}
static bool lan4(uint32_t address) {
    uint32_t ip=ntohl(address);
    return (ip&0xff000000)==0x0a000000||(ip&0xfff00000)==0xac100000||(ip&0xffff0000)==0xc0a80000||
        (ip&0xffff0000)==0xa9fe0000||(ip&0xf0000000)==0xe0000000||ip==0xffffffff;
}
static bool globalLanBypass(const uint8_t* packet,size_t length) {
    bool enabled;{std::shared_lock lk(stateMutex);enabled=globalState!=0&&globalBypassLan;}
    if(!enabled||!packet||length<20)return false;
    size_t offset;uint8_t protocol;
    if((packet[0]>>4)==4){offset=(packet[0]&15)*4;protocol=packet[9];if(offset<20||!lan4(word(packet+16)))return false;}
    else return false; // Global forwarding and LAN bypass are IPv4-only; ::1 control uses localCommunication.
    // DNS always follows SOCKS, including DNS addressed to the LAN gateway. DHCP/discovery stays local.
    return (protocol==6||protocol==17)&&offset+8<=length&&be16(packet+offset+2)!=53;
}
static bool stillBound(const Flow& f) {
    if(f.gatewayInterface)return gatewayStillBound(f);
    uint64_t birth=created(f.rule.pid);if(!birth||birth!=f.rule.created)return false;
    RouteRule r{};if(policyAction(f.rule.pid,birth,false,r,f.key.sp)!=1||r.proxy!=f.rule.proxy)return false;
    std::shared_lock lk(stateMutex);return !(r.reserved&2)||!globalBypassLan||f.key.dp==53||!lan4(f.key.dst);
}
static PROXY_CONFIG config(uint32_t id) { std::shared_lock lk(stateMutex);auto it=profiles.find(id);return it==profiles.end()?PROXY_CONFIG{}:it->second; }
static void closeGoneUdpSockets() {
    DWORD bytes=0;if(GetExtendedUdpTable(nullptr,&bytes,FALSE,AF_INET,UDP_TABLE_OWNER_MODULE,0)!=ERROR_INSUFFICIENT_BUFFER)return;
    std::vector<uint8_t> data(bytes+4096);bytes=(DWORD)data.size();
    if(GetExtendedUdpTable(data.data(),&bytes,FALSE,AF_INET,UDP_TABLE_OWNER_MODULE,0)!=NO_ERROR)return;
    auto* table=(MIB_UDPTABLE_OWNER_MODULE*)data.data();std::lock_guard lk(flowMutex);
    for(auto& [key,f]:flows)if(key.protocol==17&&f->alive&&!f->gatewayInterface){
        bool found=false;for(DWORD i=0;i<table->dwNumEntries;i++){auto&r=table->table[i];
            if(r.dwOwningPid==f->rule.pid&&ntohs((u_short)r.dwLocalPort)==key.sp&&(!r.dwLocalAddr||r.dwLocalAddr==key.src)&&
                (!f->socketCreated||(uint64_t)r.liCreateTimestamp.QuadPart==f->socketCreated)){found=true;break;}
        }
        if(!found)f->close(SocketGone);
    }
}
// TCP ownership is stable for an established 5-tuple. Re-reading the entire system
// table for every ACK/data packet stalls unrelated traffic and the relay's own SOCKS
// negotiation under load. SYN always refreshes ownership; other packets reuse it for
// at most 250 ms, and only while the exact PID+creation time is still alive.
// UDP ownership is NEVER cached across receive batches. A batch uses one fresh
// table snapshot, including socket timestamps and all wildcard/duplicate owners.
// This avoids one full system enumeration per datagram without a timed UDP cache
// that could outlive a closed/reused port.
struct OwnerBatch {
    struct Table { bool attempted=false;uint64_t at=0;std::vector<uint8_t> data; };
    std::array<Table,4> tables;uint32_t queries=0;
    const uint8_t* get(bool udp,bool ipv6) {
        auto& table=tables[(ipv6?2:0)+(udp?1:0)];uint64_t now=GetTickCount64();
        if(table.attempted&&now-table.at<2)return table.data.empty()?nullptr:table.data.data();
        table.attempted=true;table.at=now;table.data.clear();++queries;DWORD family=ipv6?AF_INET6:AF_INET;
        struct Timing{uint64_t start=qpcUs();~Timing(){auto elapsed=qpcUs()-start;++packetMetrics.ownerQueries;packetMetrics.ownerUs+=elapsed;peak(packetMetrics.ownerMaxUs,elapsed);}} timing;
        auto read=[&](void* p,DWORD* size){return udp?GetExtendedUdpTable(p,size,FALSE,family,ipv6?UDP_TABLE_OWNER_PID:UDP_TABLE_OWNER_MODULE,0):GetExtendedTcpTable(p,size,FALSE,family,TCP_TABLE_OWNER_PID_ALL,0);};
        DWORD size=0;if(read(nullptr,&size)!=ERROR_INSUFFICIENT_BUFFER)return nullptr;
        // The table may grow between sizing and retrieval; retry allocation only,
        // never sleep in the packet receiver.
        for(int i=0;i<2;i++) {
            table.data.resize((size_t)size+4096);size=(DWORD)table.data.size();DWORD rc=read(table.data.data(),&size);
            if(rc==NO_ERROR)return table.data.data();if(rc!=ERROR_INSUFFICIENT_BUFFER)break;
        }
        table.data.clear();return nullptr;
    }
};
struct TcpOwner { uint32_t pid; uint64_t birth,checked; };
static thread_local std::map<Key,TcpOwner> tcpOwners;
static uint32_t owner(const Key& k,uint64_t& socketCreated,bool freshTcp=false,OwnerBatch* batch=nullptr) {
    uint64_t now=GetTickCount64();
    if(k.protocol==6) {
        auto cached=tcpOwners.find(k);
        if(cached!=tcpOwners.end()) {
            auto identity=cached->second;
            if(!freshTcp&&now-identity.checked<250&&identity.birth&&created(identity.pid)==identity.birth)return identity.pid;
            tcpOwners.erase(cached);
        }
    }
    OwnerBatch single;auto buffer=(batch?batch:&single)->get(k.protocol==17,false);if(!buffer)return 0;
    uint32_t pid=0;
    if(k.protocol==6) { auto* t=(const MIB_TCPTABLE_OWNER_PID*)buffer;for(DWORD i=0;i<t->dwNumEntries;i++){auto& r=t->table[i];if(r.dwOwningPid&&r.dwLocalAddr==k.src&&ntohs((u_short)r.dwLocalPort)==k.sp&&r.dwRemoteAddr==k.dst&&ntohs((u_short)r.dwRemotePort)==k.dp){if(pid&&pid!=r.dwOwningPid)return UINT32_MAX;pid=r.dwOwningPid;}} }
    else {auto* t=(const MIB_UDPTABLE_OWNER_MODULE*)buffer;for(DWORD i=0;i<t->dwNumEntries;i++){auto& r=t->table[i];if(r.dwOwningPid&&(r.dwLocalAddr==k.src||r.dwLocalAddr==0)&&ntohs((u_short)r.dwLocalPort)==k.sp){if(pid&&(pid!=r.dwOwningPid||socketCreated!=(uint64_t)r.liCreateTimestamp.QuadPart))return UINT32_MAX;pid=r.dwOwningPid;socketCreated=(uint64_t)r.liCreateTimestamp.QuadPart;}}}
    if(k.protocol==6&&pid&&pid!=UINT32_MAX) {
        uint64_t birth=created(pid);
        if(birth){if(tcpOwners.size()>=4096)tcpOwners.clear();tcpOwners[k]={pid,birth,now};}
    }
    return pid;
}
static uint32_t owner6(const uint8_t* packet,int offset,uint8_t protocol,OwnerBatch* batch=nullptr) {
    bool udp=protocol==17;OwnerBatch single;auto b=(batch?batch:&single)->get(udp,true);if(!b)return 0;
    uint16_t port=be16(packet+offset);uint32_t pid=0;uint8_t zero[16]{};
    if(udp){auto*t=(const MIB_UDP6TABLE_OWNER_PID*)b;for(DWORD i=0;i<t->dwNumEntries;i++){auto&r=t->table[i];if(ntohs((u_short)r.dwLocalPort)==port&&(!memcmp(r.ucLocalAddr,packet+8,16)||!memcmp(r.ucLocalAddr,zero,16))){if(pid&&pid!=r.dwOwningPid)return UINT32_MAX;pid=r.dwOwningPid;}}}
    else {auto*t=(const MIB_TCP6TABLE_OWNER_PID*)b;for(DWORD i=0;i<t->dwNumEntries;i++){auto&r=t->table[i];if(ntohs((u_short)r.dwLocalPort)==port&&ntohs((u_short)r.dwRemotePort)==be16(packet+offset+2)&&!memcmp(r.ucLocalAddr,packet+8,16)&&!memcmp(r.ucRemoteAddr,packet+24,16)){if(pid&&pid!=r.dwOwningPid)return UINT32_MAX;pid=r.dwOwningPid;}}}
    return pid;
}
static void checksums(std::span<uint8_t> p,WINDIVERT_ADDRESS& address,bool modified) {
    if(modified||((p[0]>>4)==4&&!address.IPChecksum)||(!address.TCPChecksum&&!address.UDPChecksum))
        wdChecksums(p.data(),(UINT)p.size(),&address,0);
}
static bool inject(std::span<uint8_t> p,WINDIVERT_ADDRESS address,bool modified=true) {
    std::lock_guard lk(sendMutex); if(divert==INVALID_HANDLE_VALUE)return false;
    checksums(p,address,modified);
    if(!wdSend(divert,p.data(),(UINT)p.size(),nullptr,&address)){lastError=GetLastError();return false;}return true;
}
static SOCKET openLocal(uint32_t destination,uint16_t port,bool udp=false) {
    SOCKET s=socket(AF_INET,udp?SOCK_DGRAM:SOCK_STREAM,udp?IPPROTO_UDP:IPPROTO_TCP);if(s==INVALID_SOCKET)return s;
    if(!udp)configure_tcp_socket(s,65536,5000);
    sockaddr_in addr{};addr.sin_family=AF_INET;addr.sin_addr.s_addr=destination;addr.sin_port=htons(port);
    if(!destination||(udp?connect(s,(sockaddr*)&addr,sizeof(addr)):connect_with_timeout(s,(sockaddr*)&addr,sizeof(addr),3000))){int error=WSAGetLastError();closesocket(s);WSASetLastError(error);return INVALID_SOCKET;}return s;
}
static SOCKET dial(const PROXY_CONFIG& cfg) {
    if(!cfg.port||!cfg.host[0])return INVALID_SOCKET;
    return openLocal(resolve_hostname(cfg.host),cfg.port);
}
static bool localGatewayFlow(const Flow& f){return f.gatewayInterface&&f.rule.proxy==0;}
static uint32_t relayDestination(const Flow& f) {
    if(!localGatewayFlow(f))return f.key.dp==53?inet_addr("1.1.1.1"):f.key.dst;
    if(f.key.dp!=53||f.key.dst!=f.gatewayHost)return f.key.dst;
    // DHCP advertises the dedicated gateway as DNS. Local routes use the host's IPv4 resolver.
    ULONG size=0;GetNetworkParams(nullptr,&size);if(!size||size>65536)return inet_addr("1.1.1.1");std::vector<uint8_t> data(size);
    if(GetNetworkParams((FIXED_INFO*)data.data(),&size)==NO_ERROR)
        for(auto* dns=&((FIXED_INFO*)data.data())->DnsServerList;dns;dns=dns->Next){
            uint32_t ip=inet_addr(dns->IpAddress.String);
            if(ip&&ip!=INADDR_NONE&&(ntohl(ip)>>24)!=127&&(ntohl(ip)&0xffffff00)!=(ntohl(f.gatewayHost)&0xffffff00))return ip;
        }
    return inet_addr("1.1.1.1");
}
static void spawn(std::function<void()> task) {
    ++workers;try {std::thread([task](){try{task();}catch(...){lastError=ERROR_UNHANDLED_EXCEPTION;}--workers;}).detach();}catch(...){--workers;throw;}
}
static void udpWorker(std::shared_ptr<Flow> f) {
    // Release exited application sockets before requesting another association from the same proxy.
    closeGoneUdpSockets();if(!f->alive)return;
    bool local=localGatewayFlow(*f);auto cfg=config(f->rule.proxy);SOCKET control=INVALID_SOCKET;
    sockaddr_in relay{};
    uint32_t destination=relayDestination(*f);
    if(!local){control=dial(cfg);f->upstream=control;
        if(control==INVALID_SOCKET){f->close(DialFailed,WSAGetLastError());return;}
        WSASetLastError(0);if(socks5_udp_associate_with_config(control,&relay,&cfg)){f->close(SocksFailed,WSAGetLastError());return;}
        if(relay.sin_addr.s_addr==0)relay.sin_addr.s_addr=resolve_hostname(cfg.host);
    }
    SOCKET udp=local?openLocal(destination,f->key.dp,true):socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);f->datagram=udp;
    if(udp==INVALID_SOCKET||(!local&&connect(udp,(sockaddr*)&relay,sizeof(relay)))){f->close(UdpFailed,WSAGetLastError());return;}
    f->connected=true;trace(*f,Connected);
    configure_udp_socket(udp,262144,1000);
    while(active&&f->alive&&stillBound(*f)&&GetTickCount64()-f->last<60000) {
        std::deque<std::vector<uint8_t>> pending;
        {std::lock_guard lk(f->queueMutex);pending.swap(f->queue);}
        for(auto& payload:pending) {
            std::vector<uint8_t> msg;
            if(!local){msg.resize(10+payload.size());msg[3]=1;memcpy(msg.data()+4,&destination,4);put16(msg.data()+8,f->key.dp);memcpy(msg.data()+10,payload.data(),payload.size());}
            auto& outgoing=local?payload:msg;
            if(send(udp,outgoing.empty()?"":(char*)outgoing.data(),(int)outgoing.size(),0)==SOCKET_ERROR){f->close(SendToProxyFailed,WSAGetLastError());break;}
            f->sent+=payload.size();
            count(f->rule.pid,payload.size(),0,0,f->rule.created);
        }
        fd_set read;FD_ZERO(&read);FD_SET(udp,&read);if(!local)FD_SET(control,&read);timeval timeout{0,20000};
        int ready=select(0,&read,nullptr,nullptr,&timeout);if(ready<0)break;
        if(!local&&FD_ISSET(control,&read)){trace(*f,UpstreamEof);break;} // association lifetime is its TCP control channel
        if(!FD_ISSET(udp,&read))continue;
        uint8_t data[65535];int length=recv(udp,(char*)data,sizeof(data),0);
        int header=local?0:10;
        if(length<header)continue;
        if(!local&&(data[0]||data[1]||data[2]||data[3]!=1||word(data+4)!=destination||be16(data+8)!=f->key.dp))continue;
        int payloadLength=length-header;
        if(payloadLength>65507)continue;
        std::vector<uint8_t> response(28+payloadLength);response[0]=0x45;put16(response.data()+2,(uint16_t)response.size());response[8]=64;response[9]=17;
        memcpy(response.data()+12,&f->key.dst,4);memcpy(response.data()+16,&f->key.src,4);
        put16(response.data()+20,f->key.dp);put16(response.data()+22,f->key.sp);put16(response.data()+24,(uint16_t)(8+payloadLength));memcpy(response.data()+28,data+header,payloadLength);
        auto addr=f->address;addr.Outbound=f->gatewayInterface?1:0;addr.Loopback=0;addr.Impostor=0;
        if(f->gatewayInterface){addr.Layer=WINDIVERT_LAYER_NETWORK;addr.Network.IfIdx=f->gatewayInterface;addr.Network.SubIfIdx=0;}
        if(stillBound(*f)&&inject(response,addr)){f->received+=payloadLength;count(f->rule.pid,0,payloadLength,0,f->rule.created);}
    }
    f->close();
}
static uint32_t tcpPump(std::shared_ptr<Flow> f,SOCKET accepted,SOCKET remote,bool managed,uint32_t& error) {
    f->connected=true;trace(*f,Connected);
    configure_tcp_socket(accepted,65536,5000);configure_tcp_socket(remote,65536,5000);
    std::array<char,65536> buf{}; bool clientRead=true,proxyRead=true;
    while((active||!managed)&&f->alive&&(!managed||stillBound(*f))&&(clientRead||proxyRead)) {
        fd_set read;FD_ZERO(&read);if(clientRead)FD_SET(accepted,&read);if(proxyRead)FD_SET(remote,&read);timeval timeout{0,200000};
        int ready=select(0,&read,nullptr,nullptr,&timeout);if(ready<0){error=WSAGetLastError();return SelectFailed;}if(!ready)continue;
        if(clientRead&&FD_ISSET(accepted,&read)){
            int n=recv(accepted,buf.data(),(int)buf.size(),0);
            if(n==0){clientRead=false;f->connected=false;trace(*f,ClientEof);shutdown(remote,SD_SEND);}
            else if(n<0){error=WSAGetLastError();return ClientReadFailed;}
            else if(send_all(remote,buf.data(),n)!=n){error=WSAGetLastError();return SendToProxyFailed;}
            else {f->sent+=n;count(f->rule.pid,n,0,0,f->rule.created);}
        }
        if(proxyRead&&FD_ISSET(remote,&read)){
            int n=recv(remote,buf.data(),(int)buf.size(),0);
            if(n==0){proxyRead=false;f->connected=false;trace(*f,UpstreamEof);shutdown(accepted,SD_SEND);}
            else if(n<0){error=WSAGetLastError();return UpstreamReadFailed;}
            else if(send_all(accepted,buf.data(),n)!=n){error=WSAGetLastError();return SendToClientFailed;}
            else {f->received+=n;count(f->rule.pid,0,n,0,f->rule.created);}
        }
        f->last=GetTickCount64();
    }
    return !managed||clientRead==false&&proxyRead==false?Closed:active?PolicyClosed:EngineStopped;
}
static void tcpWorker(std::shared_ptr<Flow> f,SOCKET accepted) {
    f->client=accepted;bool local=localGatewayFlow(*f);auto cfg=config(f->rule.proxy);uint32_t destination=relayDestination(*f);
    SOCKET remote=local?openLocal(destination,f->key.dp):dial(cfg);f->upstream=remote;
    if(remote==INVALID_SOCKET){f->close(DialFailed,WSAGetLastError());return;}
    WSASetLastError(0);if(!local&&socks5_connect(remote,destination,f->key.dp,&cfg)){f->close(SocksFailed,WSAGetLastError());return;}
    if(!stillBound(*f)){f->close(PolicyClosed);return;}
    uint32_t error=0,reason=tcpPump(f,accepted,remote,true,error);
    // Keep alias briefly so FIN/RST packets still receive reverse translation.
    f->close(reason,error);
}
// Actual common relay pump, exercised with loopback socket pairs without loading WinDivert.
API uint32_t Core_RelayForTest(SOCKET accepted,SOCKET remote,uint32_t pid,uint64_t birth) {
    auto f=std::make_shared<Flow>();f->rule.pid=pid;f->rule.created=birth;f->key.protocol=6;trace(*f,Captured);
    uint32_t error=0,reason=tcpPump(f,accepted,remote,false,error);f->close(reason,error);return reason;
}
static void acceptLoop() {
    while(active) {
        sockaddr_in peer{};int len=sizeof(peer);SOCKET s=accept(listener,(sockaddr*)&peer,&len);if(s==INVALID_SOCKET)break;
        std::shared_ptr<Flow> f;
        {std::lock_guard lk(flowMutex);auto it=aliases.find(ntohs(peer.sin_port));if(it!=aliases.end()&&(it->second->gatewayInterface?it->second->key.src:it->second->key.dst)==peer.sin_addr.s_addr)f=it->second;}
        if(!f||!f->alive||f->client!=INVALID_SOCKET){closesocket(s);continue;}
        spawn([f,s](){tcpWorker(f,s);});
    }
}
static void prune() {
    uint64_t now=GetTickCount64();closeGoneUdpSockets();std::lock_guard lk(flowMutex);
    // A closed application socket can disappear from the owner table before its final FIN is captured.
    // Do not leave a relay and its connection counter alive indefinitely when that happens.
    std::set<Key> tcpSockets;bool tableValid=false;DWORD bytes=0;
    if(GetExtendedTcpTable(nullptr,&bytes,FALSE,AF_INET,TCP_TABLE_OWNER_PID_ALL,0)==ERROR_INSUFFICIENT_BUFFER){
        std::vector<uint8_t> data(bytes+4096);bytes=(DWORD)data.size();
        if(GetExtendedTcpTable(data.data(),&bytes,FALSE,AF_INET,TCP_TABLE_OWNER_PID_ALL,0)==NO_ERROR){tableValid=true;
            auto* table=(MIB_TCPTABLE_OWNER_PID*)data.data();for(DWORD i=0;i<table->dwNumEntries;i++){auto&r=table->table[i];
                if(r.dwState!=MIB_TCP_STATE_CLOSED&&r.dwState!=MIB_TCP_STATE_TIME_WAIT&&r.dwState!=MIB_TCP_STATE_DELETE_TCB)
                    tcpSockets.insert({r.dwLocalAddr,r.dwRemoteAddr,ntohs((u_short)r.dwLocalPort),ntohs((u_short)r.dwRemotePort),6});
            }
        }
    }
    for(auto it=flows.begin();it!=flows.end();) {
        auto f=it->second;
        if(f->alive&&!f->gatewayInterface&&f->key.protocol==6&&tableValid&&now-f->opened>1000&&!tcpSockets.count(f->key))f->close(SocketGone);
        if((!f->alive&&now-f->last>5000)||(f->key.protocol==17&&now-f->last>60000)||(f->key.protocol==6&&now-f->last>1800000)) {
            f->close(IdleExpired);if(f->alias)aliases.erase(f->alias);it=flows.erase(it);
        }else ++it;
    }
}
static bool localCommunication(const uint8_t* packet,size_t length) {
    if(!packet||length<20)return false;
    size_t offset=0;uint8_t protocol=0;
    if((packet[0]>>4)==4){offset=(packet[0]&15)*4;if(offset<20||packet[16]!=127)return false;protocol=packet[9];}
    else if((packet[0]>>4)==6){
        if(length<48)return false;
        for(size_t i=24;i<39;i++)if(packet[i])return false;
        if(packet[39]!=1)return false;offset=40;protocol=packet[6];
    }else return false;
    return (protocol==6||protocol==17)&&offset+8<=length&&be16(packet+offset+2)!=53;
}
API int Core_LocalCommunicationForTest(const uint8_t* packet,uint32_t length){return localCommunication(packet,length)?1:0;}
API int Core_GlobalBypassForTest(const uint8_t* packet,uint32_t length){return globalLanBypass(packet,length)?1:0;}
static void reverseTcp(Flow& f,std::span<uint8_t> p,WINDIVERT_ADDRESS& addr) {
    int offset=(p[0]&15)*4;memcpy(p.data()+12,&f.key.dst,4);memcpy(p.data()+16,&f.key.src,4);
    put16(p.data()+offset,f.key.dp);put16(p.data()+offset+2,f.key.sp);addr.Outbound=f.gatewayInterface?1:0;
    if(f.gatewayInterface){addr.Network.IfIdx=f.gatewayInterface;addr.Network.SubIfIdx=0;}
}
// Only unchanged packets enter this buffer; modified relay packets still get
// fresh checksums and immediate injection. Storage is reused across receive calls.
struct PassThroughBatch {
    std::vector<uint8_t> bytes;std::vector<WINDIVERT_ADDRESS> addresses;
    PassThroughBatch(){bytes.reserve(256*1024);addresses.reserve(64);}
    void append(std::span<uint8_t> p,WINDIVERT_ADDRESS addr) {
        checksums(p,addr,false);bytes.insert(bytes.end(),p.begin(),p.end());addresses.push_back(addr);
    }
    bool flush() {
        if(addresses.empty())return true;std::lock_guard lk(sendMutex);
        if(divert==INVALID_HANDLE_VALUE)return false;
        uint64_t begin=qpcUs();bool ok=wdSendEx(divert,bytes.data(),(UINT)bytes.size(),nullptr,0,addresses.data(),(UINT)(addresses.size()*sizeof(WINDIVERT_ADDRESS)),nullptr)!=0;
        uint64_t elapsed=qpcUs()-begin;packetMetrics.sendUs+=elapsed;peak(packetMetrics.sendMaxUs,elapsed);
        if(!ok)lastError=GetLastError();bytes.clear();addresses.clear();return ok;
    }
};
static bool processPacket(std::span<uint8_t> p,WINDIVERT_ADDRESS addr,OwnerBatch& owners,PassThroughBatch* direct=nullptr) {
    UINT length=(UINT)p.size();if(length<20)return true;
    auto pass=[&](){if(direct)direct->append(p,addr);else inject(p,addr,false);};
    {std::shared_lock lk(stateMutex);
        bool managed=globalState||!guards.empty()||std::any_of(rules.begin(),rules.end(),[](const auto&r){return r.blocked!=2;});
        // Pure inventory/direct rules need no ownership or executable lookup. Keep
        // the listener return path active for the separate VMware gateway.
        bool listenerPacket=(p[0]>>4)==4&&length>=40&&p[9]==6&&(UINT)(p[0]&15)*4+20<=length&&be16(p.data()+(p[0]&15)*4)==listenPort;
        if(!managed&&!listenerPacket){lk.unlock();pass();return true;}
    }
    // Emulator control/ADB and application IPC stay on loopback. DNS keeps the managed proxy/block policy.
    if(localCommunication(p.data(),p.size())){pass();return true;}
    if((p[0]>>4)==6) {
        if(length<40)return true;
        uint8_t protocol=p[6];int offset=40;
        while((protocol==0||protocol==43||protocol==60)&&offset+2<(int)length){int n=(p[offset+1]+1)*8;protocol=p[offset];offset+=n;}
        if((protocol!=6&&protocol!=17)||offset+8>(int)length) { // Unsupported IPv6 extensions fail closed while managing targets.
            std::shared_lock lk(stateMutex);if(!globalState&&rules.empty()&&guards.empty()){lk.unlock();pass();}return true;
        }
        uint32_t pid=owner6(p.data(),offset,protocol,&owners);if(!pid)return false;RouteRule r{};int action=policyAction(pid,created(pid),true,r,be16(p.data()+offset));
        if((r.reserved&2)&&globalLanBypass(p.data(),p.size())){pass();return true;}
        if(pid==UINT32_MAX||action==2){count(pid,0,0,length);return true;}
        pass();return true;
    }
    int ipLength=(p[0]&15)*4;if(ipLength<20||ipLength+8>(int)length)return true;
    uint8_t protocol=p[9];if(protocol!=6&&protocol!=17){pass();return true;}
    // WinDivert normally reassembles at NETWORK; never forward unclassified fragments in managed mode.
    if(be16(p.data()+6)&0x3fff){std::shared_lock lk(stateMutex);if(!globalState&&rules.empty()&&guards.empty()){lk.unlock();pass();}return true;}
    Key key{word(p.data()+12),word(p.data()+16),be16(p.data()+ipLength),be16(p.data()+ipLength+2),protocol};
    if(gatewayDhcpResponse(key,addr.Network.IfIdx)){pass();return true;}
    if(protocol==6&&key.sp==listenPort) {
        std::shared_ptr<Flow> f;{std::lock_guard lk(flowMutex);auto it=aliases.find(key.dp);if(it!=aliases.end()){auto candidate=it->second;if(candidate->gatewayInterface?candidate->key.src==key.dst&&candidate->gatewayHost==key.src:candidate->key.dst==key.dst&&candidate->key.src==key.src)f=candidate;}}
        if(f){if(!stillBound(*f)&&(ipLength+20>(int)length||!(p[ipLength+13]&5)))return true;reverseTcp(*f,p,addr);inject(p,addr);return true;}
    }
    bool freshTcp=protocol==6&&ipLength+20<=(int)length&&(p[ipLength+13]&2);
    uint64_t socketBirth=0;uint32_t pid=owner(key,socketBirth,freshTcp,&owners);
    if(!pid)return false;
    if(pid==GetCurrentProcessId()){pass();return true;}
    RouteRule rule{};int action=policyAction(pid,created(pid),false,rule,key.sp);
    if((rule.reserved&2)&&globalLanBypass(p.data(),p.size())){pass();return true;}
    if(pid==UINT32_MAX){count(pid,0,0,length);return true;}
    if(action==0||action==3){pass();return true;}
    if(action==2){count(pid,0,0,length);return true;}
    if((ntohl(key.dst)&0xf0000000)==0xe0000000||key.dst==INADDR_BROADCAST){count(pid,0,0,length);return true;}
    std::shared_ptr<Flow> f;bool fresh=false;
    {std::lock_guard lk(flowMutex);
        auto it=flows.find(key);
        if(it!=flows.end()){
            f=it->second;
            if(f->rule.pid!=rule.pid||f->rule.created!=rule.created||f->rule.proxy!=rule.proxy||f->socketCreated!=socketBirth||(!f->alive&&GetTickCount64()-f->last>1000)){
                f->close();if(f->alias)aliases.erase(f->alias);flows.erase(it);f.reset();
            }
        }
        if(!f){
            if(flows.size()>=2048||workers>=256){count(pid,0,0,length);return true;}
            if(protocol==6&&(ipLength+20>(int)length||!(p[ipLength+13]&2)))return true; // existing TCP sessions must reconnect
            f=std::make_shared<Flow>();f->key=key;f->rule=rule;f->address=addr;f->socketCreated=socketBirth;
            if(protocol==6){for(unsigned n=0;n<40000;n++){if(++nextAlias>60000)nextAlias=20000;if(!aliases.count(nextAlias)){f->alias=nextAlias;break;}}if(!f->alias)return true;aliases[f->alias]=f;}
            flows[key]=f;trace(*f,Captured);fresh=true;
        }
        f->last=GetTickCount64();
    }
    if(!f->alive)return true;
    if(protocol==6){memcpy(p.data()+12,&key.dst,4);memcpy(p.data()+16,&key.src,4);put16(p.data()+ipLength,f->alias);put16(p.data()+ipLength+2,listenPort);addr.Outbound=0;addr.Loopback=0;inject(p,addr);}
    else {
        size_t udpLength=be16(p.data()+ipLength+4);if(udpLength<8||ipLength+udpLength>length)return true;
        {std::lock_guard lk(f->queueMutex);if(f->queue.size()<128)f->queue.emplace_back(p.begin()+ipLength+8,p.begin()+ipLength+udpLength);else count(pid,0,0,length);}
        if(fresh)spawn([f](){udpWorker(f);});
    }
    return true;
}
struct DeferredPacket { std::vector<uint8_t> bytes;WINDIVERT_ADDRESS address; };
static std::mutex retryMutex;
static std::condition_variable retryWake;
static std::deque<DeferredPacket> deferredPackets;
static void deferPacket(std::span<uint8_t> p,const WINDIVERT_ADDRESS& addr) {
    std::lock_guard lk(retryMutex);
    // Unknown ownership never bypasses a managed policy, even if retries overflow.
    ++packetMetrics.unknown;
    if(deferredPackets.size()<256){deferredPackets.push_back({{p.begin(),p.end()},addr});retryWake.notify_all();}else ++packetMetrics.overflow;
}
static void retryLoop() {
    tcpOwners.clear();
    while(active) {
        DeferredPacket packet;
        {std::unique_lock lk(retryMutex);retryWake.wait(lk,[]{return !active||!deferredPackets.empty();});
            if(!active)break;packet=std::move(deferredPackets.front());deferredPackets.pop_front();}
        for(int attempt=0;attempt<3&&active;attempt++) {
            {std::unique_lock lk(retryMutex);if(retryWake.wait_for(lk,std::chrono::milliseconds(1),[]{return !active;}))break;}
            OwnerBatch fresh;if(processPacket(packet.bytes,packet.address,fresh))break;
        }
    }
}
static void pruneLoop() {
    while(active) {
        {std::unique_lock lk(retryMutex);if(retryWake.wait_for(lk,std::chrono::seconds(1),[]{return !active;}))break;}
        if(active){prune();saveTelemetry();}
    }
}
static void captureLoop() {
    tcpOwners.clear();
    std::vector<uint8_t> packets(256*1024);std::array<WINDIVERT_ADDRESS,64> addresses;PassThroughBatch direct;
    while(active) {
        UINT length=0,addrLength=(UINT)sizeof(addresses);
        if(!wdRecvEx(divert,packets.data(),(UINT)packets.size(),&length,0,addresses.data(),&addrLength,nullptr)){
            if(active)lastError=GetLastError();break;
        }
        OwnerBatch owners;void* current=packets.data();UINT remaining=length;
        ++packetMetrics.batches;
        for(UINT i=0;i<addrLength/sizeof(WINDIVERT_ADDRESS)&&remaining;i++) {
            void* next=nullptr;UINT nextLength=0;
            if(!wdParsePacket(current,remaining,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,nullptr,&next,&nextLength)||nextLength>=remaining){++packetMetrics.parseErrors;lastError=ERROR_INVALID_DATA;break;}
            std::span<uint8_t> p((uint8_t*)current,remaining-nextLength);
            ++packetMetrics.packets;packetMetrics.bytes+=p.size();packetMetrics.impostor+=addresses[i].Impostor;packetMetrics.loopback+=addresses[i].Loopback;
            if((p[0]>>4)==4&&p.size()>=20&&p[9]==1)++packetMetrics.icmp;
            LARGE_INTEGER clock,frequency;QueryPerformanceCounter(&clock);QueryPerformanceFrequency(&frequency);
            if(addresses[i].Timestamp>0&&clock.QuadPart>=addresses[i].Timestamp){uint64_t age=(clock.QuadPart-addresses[i].Timestamp)*1000000/frequency.QuadPart;packetMetrics.ageUs+=age;++packetMetrics.ageSamples;peak(packetMetrics.ageMaxUs,age);}
            uint64_t begin=qpcUs();
            if(!processPacket(p,addresses[i],owners,&direct))deferPacket(p,addresses[i]);
            uint64_t elapsed=qpcUs()-begin;packetMetrics.classifyUs+=elapsed;peak(packetMetrics.classifyMaxUs,elapsed);
            current=next;remaining=nextLength;
        }
        if(!direct.flush())break;
    }
}

#include "VmwareGateway.h"

API int Core_SetProxy(uint32_t id,const char* host,uint16_t port,const char* user,const char* password) {
    if(!id||!host||strlen(host)>253||!port||!user||!password||strlen(user)>255||strlen(password)>255)return 0;
    PROXY_CONFIG cfg{};cfg.config_id=id;cfg.type=PROXY_TYPE_SOCKS5;strcpy_s(cfg.host,host);cfg.port=port;strcpy_s(cfg.username,user);strcpy_s(cfg.password,password);
    std::unique_lock lk(stateMutex);profiles[id]=cfg;return 1;
}
static int replacePolicy(const RouteRule* entries,uint32_t countRules,const wchar_t* paths,bool replaceGuards,uint32_t nextGlobal=0,uint32_t nextState=0,uint32_t bypassLan=1,
    uint32_t bypassRemote=0,const wchar_t* images=nullptr,uint32_t nextRdpPid=0,uint64_t nextRdpCreated=0,uint32_t nextRdpPort=0) {
    if(countRules>1024||(!entries&&countRules))return 0;
    if(nextState>2||bypassLan>1||bypassRemote>1||nextRdpPort>65535||(nextState&&!nextGlobal)||(nextRdpPid&&(!nextRdpCreated||!nextRdpPort)))return 0;
    std::vector<RouteRule> next;
    if(countRules)next.assign(entries,entries+countRules);
    std::sort(next.begin(),next.end(),[](const RouteRule&a,const RouteRule&b){return std::make_pair(a.pid,a.created)<std::make_pair(b.pid,b.created);});
    for(size_t i=0;i<next.size();i++)if(!next[i].pid||!next[i].created||next[i].blocked>2||(next[i].reserved&~1u)||(i&&next[i-1].pid==next[i].pid&&next[i-1].created==next[i].created))return 0;
    std::vector<std::wstring> nextGuards;if(replaceGuards&&paths)for(auto p=paths;*p;p+=wcslen(p)+1)nextGuards.emplace_back(p);
    std::vector<std::wstring> nextImages;if(images)for(auto p=images;*p;p+=wcslen(p)+1){
        size_t length=wcslen(p);if(length<5||length>1024||nextImages.size()>=128||wcspbrk(p,L"*?\r\n")||_wcsicmp(p+length-4,L".exe")!=0)return 0;
        if(wcspbrk(p,L"\\/")&&!(length>3&&p[1]==L':'&&(p[2]==L'\\'||p[2]==L'/')))return 0;
        nextImages.emplace_back(p);
    }
    {std::unique_lock lk(stateMutex);
        if(nextState==1){auto it=profiles.find(nextGlobal);IN_ADDR ip{};if(it==profiles.end()||InetPtonA(AF_INET,it->second.host,&ip)!=1)return 0;}
        bool sameRules=rules.size()==next.size()&&std::equal(rules.begin(),rules.end(),next.begin(),[](const auto&a,const auto&b){return a.pid==b.pid&&a.created==b.created&&a.proxy==b.proxy&&a.blocked==b.blocked&&a.reserved==b.reserved;});
        if(sameRules&&(!replaceGuards||guards==nextGuards)&&globalProxy==nextGlobal&&globalState==nextState&&globalBypassLan==(bypassLan!=0)&&
            globalBypassRemote==(bypassRemote!=0)&&remoteImages==nextImages&&rdpPid==nextRdpPid&&rdpCreated==nextRdpCreated&&rdpPort==nextRdpPort)return 1;
        unrelatedProcesses.clear();
        if(replaceGuards&&guards!=nextGuards)guardResults.clear();
        rules=std::move(next);if(replaceGuards)guards.swap(nextGuards);globalProxy=nextGlobal;globalState=nextState;globalBypassLan=bypassLan!=0;
        updateProcessJobs();
        for(const auto& r:rules)if(r.reserved&1) {
            auto key=std::make_pair(r.pid,r.created);
            if(processJobs.find(key)!=processJobs.end())continue;
            if(processLifetimes.find(key)!=processLifetimes.end())continue;
            HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,r.pid);FILETIME c,e,k,u;
            if(p&&GetProcessTimes(p,&c,&e,&k,&u)&&(((uint64_t)c.dwHighDateTime<<32)|c.dwLowDateTime)==r.created)
                processLifetimes[key]={p,0};
            else if(p)CloseHandle(p);
        }
        for(auto it=processLifetimes.begin();it!=processLifetimes.end();) {
            bool keep=processJobs.find(it->first)==processJobs.end()&&std::any_of(rules.begin(),rules.end(),[&](const auto&r){return r.pid==it->first.first&&r.created==it->first.second&&(r.reserved&1);});
            if(!keep){if(it->second.handle)CloseHandle(it->second.handle);it=processLifetimes.erase(it);}
            else {auto& lifetime=it->second;if(lifetime.handle){FILETIME c,e,k,u;if(GetProcessTimes(lifetime.handle,&c,&e,&k,&u))lifetime.exited=((uint64_t)e.dwHighDateTime<<32)|e.dwLowDateTime;
                if(lifetime.exited){CloseHandle(lifetime.handle);lifetime.handle=nullptr;}}++it;}
        }
        if(remoteImages!=nextImages||globalBypassRemote!=(bypassRemote!=0))++remoteGeneration;
        remoteImages=std::move(nextImages);globalBypassRemote=bypassRemote!=0;rdpPid=nextRdpPid;rdpCreated=nextRdpCreated;rdpPort=nextRdpPort;
        for(auto it=descendants.begin();it!=descendants.end();) {
            bool keep=std::any_of(rules.begin(),rules.end(),[&](const auto&r){return r.pid==it->second.first&&r.created==it->second.second&&(r.reserved&1);});
            if(!keep||created(it->first.first)!=it->first.second)it=descendants.erase(it);else ++it;
        }
    }
    std::lock_guard lk(flowMutex);for(auto& [key,f]:flows)if(!stillBound(*f))f->close(PolicyClosed);
    return 1;
}
API int Core_ReplaceRules(const RouteRule* entries,uint32_t countRules){return replacePolicy(entries,countRules,nullptr,false);}
API int Core_ReplacePolicy(const RouteRule* entries,uint32_t countRules,const wchar_t* paths){return replacePolicy(entries,countRules,paths,true);}
API int Core_ReplacePolicyEx(const RouteRule* entries,uint32_t countRules,const wchar_t* paths,uint32_t proxy,uint32_t state,uint32_t bypassLan){return replacePolicy(entries,countRules,paths,true,proxy,state,bypassLan);}
API int Core_ReplacePolicyEx2(const RouteRule* entries,uint32_t countRules,const wchar_t* paths,uint32_t proxy,uint32_t state,uint32_t bypassLan,uint32_t bypassRemote,const wchar_t* images,uint32_t remotePid,uint64_t remoteCreated,uint32_t remotePort){return replacePolicy(entries,countRules,paths,true,proxy,state,bypassLan,bypassRemote,images,remotePid,remoteCreated,remotePort);}
API uint32_t Core_GetDescendants(uint32_t pid,uint64_t birth,RouteRule* output,uint32_t capacity){
    if(!output||!capacity)return 0;capacity=(std::min)(capacity,1024u);std::unique_lock lk(stateMutex);uint32_t n=0;
    bool followed=std::any_of(rules.begin(),rules.end(),[&](const auto&r){return r.pid==pid&&r.created==birth&&(r.reserved&1);});
    if(followed)for(const auto& child:jobMembers(pid,birth))if(descendants.size()<4096)descendants.try_emplace(child,std::make_pair(pid,birth));
    for(const auto& [child,parent]:descendants)if(parent==std::make_pair(pid,birth)&&created(child.first)==child.second&&n<capacity)output[n++]={child.first,0,child.second,0,0};return n;
}
API int Core_ProcessHandoffMode(uint32_t pid,uint64_t birth){std::shared_lock lk(stateMutex);return processJobs.find({pid,birth})!=processJobs.end()?2:processLifetimes.find({pid,birth})!=processLifetimes.end()?1:0;}
API int Core_SetOwner(const wchar_t* sid){PSID parsed=nullptr;if(!ConvertStringSidToSidW(sid,&parsed))return 0;DWORD size=GetLengthSid(parsed);std::unique_lock lk(stateMutex);ownerSid.assign((uint8_t*)parsed,(uint8_t*)parsed+size);unrelatedProcesses.clear();guardResults.clear();LocalFree(parsed);return 1;}
API int Core_RouteForTest(uint32_t pid,uint64_t birth,int ipv6,uint32_t* proxy) {RouteRule r{};int action=policyAction(pid,birth,ipv6!=0,r);if(proxy)*proxy=r.proxy;return action;}
API int Core_RoutePacketForTest(uint32_t pid,uint64_t birth,int ipv6,uint16_t localPort,uint32_t* proxy) {RouteRule r{};int action=policyAction(pid,birth,ipv6!=0,r,localPort);if(proxy)*proxy=r.proxy;return action;}
API void Core_ClearGuards(){std::unique_lock lk(stateMutex);guards.clear();guardResults.clear();}
API void Core_AddGuard(const wchar_t* path){if(!path)return;std::unique_lock lk(stateMutex);for(const auto& g:guards)if(_wcsicmp(g.c_str(),path)==0)return;guards.emplace_back(path);guardResults.clear();}
API void Core_SetGuards(const wchar_t* paths){std::vector<std::wstring> next;if(paths){for(auto p=paths;*p;p+=wcslen(p)+1)next.emplace_back(p);}std::unique_lock lk(stateMutex);if(guards!=next){guards.swap(next);guardResults.clear();}}
API void Core_GetTraffic(uint32_t pid,Traffic* output){if(!output)return;*output={};std::shared_lock lk(stateMutex);for(const auto& [key,t]:traffic)if(key.first==pid){output->sent+=t.sent;output->received+=t.received;output->dropped+=t.dropped;}}
API void Core_GetProcessStats(uint32_t pid,uint64_t birth,Traffic* output,uint32_t* tcp,uint32_t* udp){
    if(output){std::shared_lock lk(stateMutex);auto it=traffic.find({pid,birth});*output=it==traffic.end()?Traffic{}:it->second;}
    uint32_t t=0,u=0;{std::lock_guard lk(flowMutex);for(const auto& [key,f]:flows)if(f->alive&&f->connected&&f->rule.pid==pid&&f->rule.created==birth){if(key.protocol==6)++t;else if(key.protocol==17)++u;}}
    if(tcp)*tcp=t;if(udp)*udp=u;
}
API uint32_t Core_GetFlowEvents(FlowEvent* output,uint32_t capacity){
    if(!output||!capacity)return 0;capacity=(std::min)(capacity,1024u);std::lock_guard lk(eventMutex);
    uint32_t n=(uint32_t)(std::min)((size_t)capacity,events.size());std::copy(events.end()-n,events.end(),output);return n;
}
API uint32_t Core_LastError(){return lastError;}
API int Core_IsRunning(){return active&&lastError==0;}
API int Core_Start(const wchar_t* libraryPath) {
    std::lock_guard lifecycle(lifecycleMutex);if(active)return 1;
    lastError=0;if(!queryProcessInfo()){lastError=ERROR_PROC_NOT_FOUND;return 0;}
    WSADATA ws{};int startup=WSAStartup(MAKEWORD(2,2),&ws);if(startup){lastError=startup;return 0;}
    auto failed=[&](){if(listener!=INVALID_SOCKET){closesocket(listener);listener=INVALID_SOCKET;}if(driverLibrary){FreeLibrary(driverLibrary);driverLibrary=nullptr;}WSACleanup();return 0;};
    driverLibrary=LoadLibraryExW(libraryPath,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!driverLibrary){lastError=GetLastError();return failed();}
#define LOAD(name) wd##name=(decltype(wd##name))GetProcAddress(driverLibrary,"WinDivert" #name);if(!wd##name){lastError=ERROR_PROC_NOT_FOUND;return failed();}
    LOAD(Open) LOAD(Recv) LOAD(RecvEx) LOAD(Send) LOAD(SendEx) LOAD(Close) LOAD(Shutdown)
#undef LOAD
    wdChecksums=(decltype(wdChecksums))GetProcAddress(driverLibrary,"WinDivertHelperCalcChecksums");if(!wdChecksums){lastError=ERROR_PROC_NOT_FOUND;return failed();}
    wdParsePacket=(decltype(wdParsePacket))GetProcAddress(driverLibrary,"WinDivertHelperParsePacket");if(!wdParsePacket){lastError=ERROR_PROC_NOT_FOUND;return failed();}
    listener=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);sockaddr_in local{};local.sin_family=AF_INET;local.sin_addr.s_addr=INADDR_ANY;
    if(bind(listener,(sockaddr*)&local,sizeof(local))||listen(listener,SOMAXCONN)){lastError=WSAGetLastError();return failed();}
    int len=sizeof(local);getsockname(listener,(sockaddr*)&local,&len);listenPort=ntohs(local.sin_port);
    divert=wdOpen("outbound and (ip or ipv6)",WINDIVERT_LAYER_NETWORK,100,0);
    if(divert==INVALID_HANDLE_VALUE){lastError=GetLastError();return failed();}
    {std::lock_guard lk(retryMutex);deferredPackets.clear();}
    auto safe=[](auto task){return std::thread([task](){try{task();}catch(...){lastError=ERROR_UNHANDLED_EXCEPTION;}});};
    startTelemetry();active=true;captureThread=safe(captureLoop);acceptThread=safe(acceptLoop);pruneThread=safe(pruneLoop);retryThread=safe(retryLoop);return 1;
}
API void Core_Stop() {
    std::lock_guard lifecycle(lifecycleMutex);stopGateway();if(!active.exchange(false))return;
    closesocket(listener);listener=INVALID_SOCKET;
    {std::lock_guard lk(flowMutex);for(auto& [k,f]:flows)f->close(EngineStopped);}
    wdShutdown(divert,WINDIVERT_SHUTDOWN_RECV);
    retryWake.notify_all();
    if(captureThread.joinable())captureThread.join();if(acceptThread.joinable())acceptThread.join();
    if(pruneThread.joinable())pruneThread.join();if(retryThread.joinable())retryThread.join();
    saveTelemetry();telemetryPath.clear();
    {std::lock_guard lk(retryMutex);deferredPackets.clear();}
    while(workers)Sleep(10);
    {std::lock_guard lk(sendMutex);wdClose(divert);divert=INVALID_HANDLE_VALUE;}
    {std::lock_guard lk(flowMutex);flows.clear();aliases.clear();}
    if(driverLibrary){FreeLibrary(driverLibrary);driverLibrary=nullptr;}WSACleanup();
}
API int Core_Connect(SOCKET socket,const char* destination,uint16_t port,const char* user,const char* password) {
    if(!destination||!user||!password||strlen(user)>255||strlen(password)>255)return -1;
    PROXY_CONFIG cfg{};strcpy_s(cfg.username,user);strcpy_s(cfg.password,password);
    int result=socks5_connect_domain(socket,destination,port,&cfg);SecureZeroMemory(&cfg,sizeof(cfg));return result;
}
API int Core_Associate(SOCKET socket,const char* user,const char* password,uint32_t* ip,uint16_t* port) {
    if(!user||!password||!ip||!port||strlen(user)>255||strlen(password)>255)return -1;
    PROXY_CONFIG cfg{};strcpy_s(cfg.username,user);strcpy_s(cfg.password,password);sockaddr_in relay{};
    int result=socks5_udp_associate_with_config(socket,&relay,&cfg);*ip=relay.sin_addr.s_addr;*port=ntohs(relay.sin_port);SecureZeroMemory(&cfg,sizeof(cfg));return result;
}
