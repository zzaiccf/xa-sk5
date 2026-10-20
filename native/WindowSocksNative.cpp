// WindowSocks routing engine. SOCKS handshakes are linked from the pinned MIT ProxyBridge sources.
// No WinDivert import library or driver is needed to build or run protocol/policy tests.
#include <winsock2.h>
#include <windows.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <sddl.h>
#include <tlhelp32.h>
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
static void trace(const Flow& f,uint32_t stage,uint32_t error=0);
static std::atomic<uint64_t> nextFlow{0},nextEvent{0};
static std::mutex eventMutex;
static std::deque<FlowEvent> events;
struct Key {
    uint32_t src, dst; uint16_t sp, dp; uint8_t protocol;
    bool operator<(const Key& b) const { return std::tie(src,dst,sp,dp,protocol) < std::tie(b.src,b.dst,b.sp,b.dp,b.protocol); }
};
struct Flow {
    uint64_t id=++nextFlow,opened=GetTickCount64();
    Key key{}; RouteRule rule{}; uint16_t alias{}; uint64_t socketCreated{}; WINDIVERT_ADDRESS address{};
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
static std::map<uint32_t, PROXY_CONFIG> profiles;
static std::map<std::pair<uint32_t,uint64_t>, Traffic> traffic;
static std::mutex flowMutex, sendMutex, lifecycleMutex;
static std::map<Key,std::shared_ptr<Flow>> flows;
static std::map<uint16_t,std::shared_ptr<Flow>> aliases;
static std::atomic<bool> active{false};
static std::atomic<unsigned> workers{0};
static std::thread captureThread, acceptThread;
static SOCKET listener=INVALID_SOCKET;
static uint16_t listenPort=0, nextAlias=20000;
static HANDLE divert=INVALID_HANDLE_VALUE;
static HMODULE driverLibrary=nullptr;
static decltype(&WinDivertOpen) wdOpen;
static decltype(&WinDivertRecv) wdRecv;
static decltype(&WinDivertSend) wdSend;
static decltype(&WinDivertClose) wdClose;
static decltype(&WinDivertHelperCalcChecksums) wdChecksums;
static decltype(&WinDivertShutdown) wdShutdown;
static std::atomic<uint32_t> lastError{0};

static uint16_t be16(const uint8_t* p) { return (uint16_t)((p[0]<<8)|p[1]); }
static void put16(uint8_t* p,uint16_t x) { p[0]=(uint8_t)(x>>8);p[1]=(uint8_t)x; }
static uint32_t word(const uint8_t* p) { uint32_t x;memcpy(&x,p,4);return x; }
static uint64_t created(uint32_t pid) {
    HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid); if(!p) return 0;
    FILETIME c,e,k,u; bool ok=GetProcessTimes(p,&c,&e,&k,&u)!=0;CloseHandle(p);
    return ok?((uint64_t)c.dwHighDateTime<<32)|c.dwLowDateTime:0;
}
static bool guarded(uint32_t pid) {
    {std::shared_lock lk(stateMutex);if(guards.empty())return false;}
    HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(!p)return false;
    std::shared_lock lk(stateMutex);
    if(!ownerSid.empty()) {
        HANDLE token=nullptr; bool owned=false;
        if(OpenProcessToken(p,TOKEN_QUERY,&token)){DWORD bytes=0;GetTokenInformation(token,TokenUser,nullptr,0,&bytes);std::vector<uint8_t> info(bytes);if(bytes&&GetTokenInformation(token,TokenUser,info.data(),bytes,&bytes))owned=EqualSid(((TOKEN_USER*)info.data())->User.Sid,ownerSid.data())!=0;CloseHandle(token);}
        if(!owned){CloseHandle(p);return false;}
    }
    wchar_t path[32768]; DWORD n=32768;bool ok=QueryFullProcessImageNameW(p,0,path,&n)!=0;CloseHandle(p);
    if(!ok)return false;
    for(const auto& g:guards) if(_wcsicmp(path,g.c_str())==0) return true;
    return false;
}
static int route(uint32_t pid,uint64_t birth,bool ipv6,RouteRule& found) {
    if(pid==GetCurrentProcessId())return 0;
    std::unique_lock lk(stateMutex);
    auto action=[&](const RouteRule& r){found=r;found.pid=pid;found.created=birth;return r.blocked==2?3:r.blocked||ipv6||profiles.find(r.proxy)==profiles.end()?2:1;};
    for(const auto& r:rules) if(r.pid==pid && r.created==birth && birth) {
        return action(r);
    }
    if(!birth)return 0;
    if(std::none_of(rules.begin(),rules.end(),[](const auto&r){return (r.reserved&1)!=0;}))return 0;
    if(!ownerSid.empty()) {
        HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid),token=nullptr;bool owned=false;
        if(p&&OpenProcessToken(p,TOKEN_QUERY,&token)){DWORD bytes=0;GetTokenInformation(token,TokenUser,nullptr,0,&bytes);std::vector<uint8_t> info(bytes);
            if(bytes&&GetTokenInformation(token,TokenUser,info.data(),bytes,&bytes))owned=EqualSid(((TOKEN_USER*)info.data())->User.Sid,ownerSid.data())!=0;CloseHandle(token);}
        if(p)CloseHandle(p);if(!owned)return 0;
    }
    auto inherited=descendants.find({pid,birth});
    if(inherited!=descendants.end())for(const auto& r:rules)if(r.pid==inherited->second.first&&r.created==inherited->second.second&&(r.reserved&1))return action(r);
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);if(snapshot==INVALID_HANDLE_VALUE)return 0;
    std::map<uint32_t,uint32_t> parents;PROCESSENTRY32W entry{};entry.dwSize=sizeof(entry);
    if(Process32FirstW(snapshot,&entry))do{parents[entry.th32ProcessID]=entry.th32ParentProcessID;}while(Process32NextW(snapshot,&entry));CloseHandle(snapshot);
    uint32_t child=pid;uint64_t childBirth=birth;
    for(int depth=0;depth<32;depth++) {
        auto it=parents.find(child);if(it==parents.end()||!it->second||it->second==child)break;
        child=it->second;uint64_t parentBirth=created(child);if(!parentBirth||parentBirth>childBirth)break;childBirth=parentBirth;
        for(const auto& r:rules)if(r.pid==child&&r.created==parentBirth&&(r.reserved&1)){
            if(descendants.size()<4096)descendants[{pid,birth}]={r.pid,r.created};
            return action(r);
        }
    }
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
    for(auto& [key,f]:flows)if(key.protocol==17&&f->alive){
        bool found=false;for(DWORD i=0;i<table->dwNumEntries;i++){auto&r=table->table[i];
            if(r.dwOwningPid==f->rule.pid&&ntohs((u_short)r.dwLocalPort)==key.sp&&(!r.dwLocalAddr||r.dwLocalAddr==key.src)&&
                (!f->socketCreated||(uint64_t)r.liCreateTimestamp.QuadPart==f->socketCreated)){found=true;break;}
        }
        if(!found)f->close(SocketGone);
    }
}
static uint32_t owner(const Key& k,uint64_t& socketCreated) {
    DWORD size=0; DWORD rc=k.protocol==17?GetExtendedUdpTable(nullptr,&size,FALSE,AF_INET,UDP_TABLE_OWNER_MODULE,0):GetExtendedTcpTable(nullptr,&size,FALSE,AF_INET,TCP_TABLE_OWNER_PID_ALL,0);
    if(rc!=ERROR_INSUFFICIENT_BUFFER)return 0;
    std::vector<uint8_t> buffer(size+4096);size=(DWORD)buffer.size();
    rc=k.protocol==17?GetExtendedUdpTable(buffer.data(),&size,FALSE,AF_INET,UDP_TABLE_OWNER_MODULE,0):GetExtendedTcpTable(buffer.data(),&size,FALSE,AF_INET,TCP_TABLE_OWNER_PID_ALL,0);
    if(rc!=NO_ERROR)return 0;
    uint32_t pid=0;
    if(k.protocol==6) { auto* t=(MIB_TCPTABLE_OWNER_PID*)buffer.data();for(DWORD i=0;i<t->dwNumEntries;i++){auto& r=t->table[i];if(r.dwOwningPid&&r.dwLocalAddr==k.src&&ntohs((u_short)r.dwLocalPort)==k.sp&&r.dwRemoteAddr==k.dst&&ntohs((u_short)r.dwRemotePort)==k.dp){if(pid&&pid!=r.dwOwningPid)return UINT32_MAX;pid=r.dwOwningPid;}} }
    else {auto* t=(MIB_UDPTABLE_OWNER_MODULE*)buffer.data();for(DWORD i=0;i<t->dwNumEntries;i++){auto& r=t->table[i];if(r.dwOwningPid&&(r.dwLocalAddr==k.src||r.dwLocalAddr==0)&&ntohs((u_short)r.dwLocalPort)==k.sp){if(pid&&(pid!=r.dwOwningPid||socketCreated!=(uint64_t)r.liCreateTimestamp.QuadPart))return UINT32_MAX;pid=r.dwOwningPid;socketCreated=(uint64_t)r.liCreateTimestamp.QuadPart;}}}
    return pid;
}
static uint32_t owner6(const uint8_t* packet,int offset,uint8_t protocol) {
    DWORD size=0;bool udp=protocol==17;
    auto get=[&](void* p,DWORD* n){return udp?GetExtendedUdpTable(p,n,FALSE,AF_INET6,UDP_TABLE_OWNER_PID,0):GetExtendedTcpTable(p,n,FALSE,AF_INET6,TCP_TABLE_OWNER_PID_ALL,0);};
    if(get(nullptr,&size)!=ERROR_INSUFFICIENT_BUFFER)return 0;
    std::vector<uint8_t>b(size+4096);size=(DWORD)b.size();if(get(b.data(),&size)!=NO_ERROR)return 0;
    uint16_t port=be16(packet+offset);uint32_t pid=0;uint8_t zero[16]{};
    if(udp){auto*t=(MIB_UDP6TABLE_OWNER_PID*)b.data();for(DWORD i=0;i<t->dwNumEntries;i++){auto&r=t->table[i];if(ntohs((u_short)r.dwLocalPort)==port&&(!memcmp(r.ucLocalAddr,packet+8,16)||!memcmp(r.ucLocalAddr,zero,16))){if(pid&&pid!=r.dwOwningPid)return UINT32_MAX;pid=r.dwOwningPid;}}}
    else {auto*t=(MIB_TCP6TABLE_OWNER_PID*)b.data();for(DWORD i=0;i<t->dwNumEntries;i++){auto&r=t->table[i];if(ntohs((u_short)r.dwLocalPort)==port&&ntohs((u_short)r.dwRemotePort)==be16(packet+offset+2)&&!memcmp(r.ucLocalAddr,packet+8,16)&&!memcmp(r.ucRemoteAddr,packet+24,16))pid=r.dwOwningPid;}}
    return pid;
}
static bool inject(std::vector<uint8_t>& p,WINDIVERT_ADDRESS address) {
    std::lock_guard lk(sendMutex); if(divert==INVALID_HANDLE_VALUE)return false;
    wdChecksums(p.data(),(UINT)p.size(),&address,0);
    if(!wdSend(divert,p.data(),(UINT)p.size(),nullptr,&address)){lastError=GetLastError();return false;}return true;
}
static SOCKET dial(const PROXY_CONFIG& cfg) {
    if(!cfg.port||!cfg.host[0])return INVALID_SOCKET;
    SOCKET s=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);if(s==INVALID_SOCKET)return s;
    configure_tcp_socket(s,65536,5000);
    sockaddr_in addr{};addr.sin_family=AF_INET;addr.sin_addr.s_addr=resolve_hostname(cfg.host);addr.sin_port=htons(cfg.port);
    if(!addr.sin_addr.s_addr||connect_with_timeout(s,(sockaddr*)&addr,sizeof(addr),3000)){int error=WSAGetLastError();closesocket(s);WSASetLastError(error);return INVALID_SOCKET;}return s;
}
static void spawn(std::function<void()> task) {
    ++workers;try {std::thread([task](){try{task();}catch(...){lastError=ERROR_UNHANDLED_EXCEPTION;}--workers;}).detach();}catch(...){--workers;throw;}
}
static void udpWorker(std::shared_ptr<Flow> f) {
    // Release exited application sockets before requesting another association from the same proxy.
    closeGoneUdpSockets();if(!f->alive)return;
    auto cfg=config(f->rule.proxy);SOCKET control=dial(cfg);f->upstream=control;
    sockaddr_in relay{};
    if(control==INVALID_SOCKET){f->close(DialFailed,WSAGetLastError());return;}
    WSASetLastError(0);if(socks5_udp_associate_with_config(control,&relay,&cfg)){f->close(SocksFailed,WSAGetLastError());return;}
    if(relay.sin_addr.s_addr==0)relay.sin_addr.s_addr=resolve_hostname(cfg.host);
    SOCKET udp=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);f->datagram=udp;
    if(udp==INVALID_SOCKET||connect(udp,(sockaddr*)&relay,sizeof(relay))){f->close(UdpFailed,WSAGetLastError());return;}
    f->connected=true;trace(*f,Connected);
    configure_udp_socket(udp,262144,1000);
    while(active&&f->alive&&stillBound(*f)&&GetTickCount64()-f->last<60000) {
        std::deque<std::vector<uint8_t>> pending;
        {std::lock_guard lk(f->queueMutex);pending.swap(f->queue);}
        for(auto& payload:pending) {
            std::vector<uint8_t> msg(10+payload.size());msg[3]=1;
            uint32_t destination=f->key.dp==53?inet_addr("1.1.1.1"):f->key.dst;
            memcpy(msg.data()+4,&destination,4);put16(msg.data()+8,f->key.dp);memcpy(msg.data()+10,payload.data(),payload.size());
            if(send(udp,(char*)msg.data(),(int)msg.size(),0)==SOCKET_ERROR){f->close(SendToProxyFailed,WSAGetLastError());break;}
            f->sent+=payload.size();
            count(f->rule.pid,payload.size(),0,0,f->rule.created);
        }
        fd_set read;FD_ZERO(&read);FD_SET(udp,&read);FD_SET(control,&read);timeval timeout{0,20000};
        int ready=select(0,&read,nullptr,nullptr,&timeout);if(ready<0)break;
        if(FD_ISSET(control,&read)){trace(*f,UpstreamEof);break;} // association lifetime is its TCP control channel
        if(!FD_ISSET(udp,&read))continue;
        uint8_t data[65535];int length=recv(udp,(char*)data,sizeof(data),0);
        if(length<10||data[0]||data[1]||data[2]||data[3]!=1)continue;
        uint32_t expected=f->key.dp==53?inet_addr("1.1.1.1"):f->key.dst;
        if(word(data+4)!=expected||be16(data+8)!=f->key.dp)continue;
        int payloadLength=length-10;
        if(payloadLength>65507)continue;
        std::vector<uint8_t> response(28+payloadLength);response[0]=0x45;put16(response.data()+2,(uint16_t)response.size());response[8]=64;response[9]=17;
        memcpy(response.data()+12,&f->key.dst,4);memcpy(response.data()+16,&f->key.src,4);
        put16(response.data()+20,f->key.dp);put16(response.data()+22,f->key.sp);put16(response.data()+24,(uint16_t)(8+payloadLength));memcpy(response.data()+28,data+10,payloadLength);
        auto addr=f->address;addr.Outbound=0;addr.Loopback=0;addr.Impostor=0;
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
    f->client=accepted;auto cfg=config(f->rule.proxy);SOCKET remote=dial(cfg);f->upstream=remote;
    uint32_t destination=f->key.dp==53?inet_addr("1.1.1.1"):f->key.dst;
    if(remote==INVALID_SOCKET){f->close(DialFailed,WSAGetLastError());return;}
    WSASetLastError(0);if(socks5_connect(remote,destination,f->key.dp,&cfg)){f->close(SocksFailed,WSAGetLastError());return;}
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
        {std::lock_guard lk(flowMutex);auto it=aliases.find(ntohs(peer.sin_port));if(it!=aliases.end()&&it->second->key.dst==peer.sin_addr.s_addr)f=it->second;}
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
        if(f->alive&&f->key.protocol==6&&tableValid&&now-f->opened>1000&&!tcpSockets.count(f->key))f->close(SocketGone);
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
static void captureLoop() {
    uint64_t lastPrune=0;
    while(active) {
        std::vector<uint8_t> p(65535);UINT length=0;WINDIVERT_ADDRESS addr{};
        if(!wdRecv(divert,p.data(),(UINT)p.size(),&length,&addr)){if(active)lastError=GetLastError();break;}
        p.resize(length);if(length<20)continue;
        if(GetTickCount64()-lastPrune>1000){prune();lastPrune=GetTickCount64();}
        // Emulator control/ADB and application IPC stay on loopback. DNS keeps the managed proxy/block policy.
        if(localCommunication(p.data(),p.size())){inject(p,addr);continue;}
        if((p[0]>>4)==6) {
            if(length<40)continue;
            uint8_t protocol=p[6];int offset=40;
            while((protocol==0||protocol==43||protocol==60)&&offset+2<(int)length){int n=(p[offset+1]+1)*8;protocol=p[offset];offset+=n;}
            if((protocol!=6&&protocol!=17)||offset+8>(int)length) { // Unsupported IPv6 extensions fail closed while managing targets.
                std::shared_lock lk(stateMutex);if(!globalState&&rules.empty()&&guards.empty()){lk.unlock();inject(p,addr);}continue;
            }
            uint32_t pid=owner6(p.data(),offset,protocol);RouteRule r{};int action=policyAction(pid,created(pid),true,r,be16(p.data()+offset));
            if((r.reserved&2)&&globalLanBypass(p.data(),p.size())){inject(p,addr);continue;}
            if(!pid){std::shared_lock lk(stateMutex);if(globalState||!rules.empty()||!guards.empty())continue;}
            if(pid==UINT32_MAX||action==2){count(pid,0,0,length);continue;}
            inject(p,addr);continue;
        }
        int ipLength=(p[0]&15)*4;if(ipLength<20||ipLength+8>(int)length)continue;
        uint8_t protocol=p[9];if(protocol!=6&&protocol!=17){inject(p,addr);continue;}
        // WinDivert normally reassembles at NETWORK; never forward unclassified fragments in managed mode.
        if(be16(p.data()+6)&0x3fff){std::shared_lock lk(stateMutex);if(!globalState&&rules.empty()&&guards.empty()){lk.unlock();inject(p,addr);}continue;}
        Key key{word(p.data()+12),word(p.data()+16),be16(p.data()+ipLength),be16(p.data()+ipLength+2),protocol};
        if(protocol==6&&key.sp==listenPort) {
            std::shared_ptr<Flow> f;{std::lock_guard lk(flowMutex);auto it=aliases.find(key.dp);if(it!=aliases.end()&&it->second->key.dst==key.dst&&it->second->key.src==key.src)f=it->second;}
            if(f){if(!stillBound(*f)&&(ipLength+20>(int)length||!(p[ipLength+13]&5)))continue;memcpy(p.data()+12,&f->key.dst,4);memcpy(p.data()+16,&f->key.src,4);put16(p.data()+ipLength,f->key.dp);put16(p.data()+ipLength+2,f->key.sp);addr.Outbound=0;inject(p,addr);continue;}
        }
        uint64_t socketBirth=0;uint32_t pid=owner(key,socketBirth);
        if(!pid){for(int i=0;i<3&&!pid;i++){Sleep(1);pid=owner(key,socketBirth);}}
        if(pid==GetCurrentProcessId()){inject(p,addr);continue;}
        RouteRule rule{};int action=policyAction(pid,created(pid),false,rule,key.sp);
        if((rule.reserved&2)&&globalLanBypass(p.data(),p.size())){inject(p,addr);continue;}
        if(pid==UINT32_MAX){count(pid,0,0,length);continue;}
        if(!pid){std::shared_lock lk(stateMutex);if(globalState||!rules.empty()||!guards.empty())continue;}
        if(action==0||action==3){inject(p,addr);continue;}
        if(action==2){count(pid,0,0,length);continue;}
        if((ntohl(key.dst)&0xf0000000)==0xe0000000||key.dst==INADDR_BROADCAST){count(pid,0,0,length);continue;}
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
                if(flows.size()>=2048||workers>=256){count(pid,0,0,length);continue;}
                if(protocol==6&&(ipLength+20>(int)length||!(p[ipLength+13]&2)))continue; // existing TCP sessions must reconnect
                f=std::make_shared<Flow>();f->key=key;f->rule=rule;f->address=addr;f->socketCreated=socketBirth;
                if(protocol==6){for(unsigned n=0;n<40000;n++){if(++nextAlias>60000)nextAlias=20000;if(!aliases.count(nextAlias)){f->alias=nextAlias;break;}}if(!f->alias)continue;aliases[f->alias]=f;}
                flows[key]=f;trace(*f,Captured);fresh=true;
            }
            f->last=GetTickCount64();
        }
        if(!f->alive)continue;
        if(protocol==6){memcpy(p.data()+12,&key.dst,4);memcpy(p.data()+16,&key.src,4);put16(p.data()+ipLength,f->alias);put16(p.data()+ipLength+2,listenPort);addr.Outbound=0;addr.Loopback=0;inject(p,addr);}
        else {
            size_t udpLength=be16(p.data()+ipLength+4);if(udpLength<8||ipLength+udpLength>length)continue;
            {std::lock_guard lk(f->queueMutex);if(f->queue.size()<128)f->queue.emplace_back(p.begin()+ipLength+8,p.begin()+ipLength+udpLength);else count(pid,0,0,length);}
            if(fresh)spawn([f](){udpWorker(f);});
        }
    }
}

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
    std::sort(next.begin(),next.end(),[](const RouteRule&a,const RouteRule&b){return a.pid<b.pid;});
    for(size_t i=0;i<next.size();i++)if(!next[i].pid||!next[i].created||next[i].blocked>2||(next[i].reserved&~1u)||(i&&next[i-1].pid==next[i].pid))return 0;
    std::vector<std::wstring> nextGuards;if(replaceGuards&&paths)for(auto p=paths;*p;p+=wcslen(p)+1)nextGuards.emplace_back(p);
    std::vector<std::wstring> nextImages;if(images)for(auto p=images;*p;p+=wcslen(p)+1){
        size_t length=wcslen(p);if(length<5||length>1024||nextImages.size()>=128||wcspbrk(p,L"*?\r\n")||_wcsicmp(p+length-4,L".exe")!=0)return 0;
        if(wcspbrk(p,L"\\/")&&!(length>3&&p[1]==L':'&&(p[2]==L'\\'||p[2]==L'/')))return 0;
        nextImages.emplace_back(p);
    }
    {std::unique_lock lk(stateMutex);
        if(nextState==1){auto it=profiles.find(nextGlobal);IN_ADDR ip{};if(it==profiles.end()||InetPtonA(AF_INET,it->second.host,&ip)!=1)return 0;}
        rules=std::move(next);if(replaceGuards)guards.swap(nextGuards);globalProxy=nextGlobal;globalState=nextState;globalBypassLan=bypassLan!=0;
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
    if(!output||!capacity)return 0;capacity=(std::min)(capacity,1024u);std::shared_lock lk(stateMutex);uint32_t n=0;
    for(const auto& [child,parent]:descendants)if(parent==std::make_pair(pid,birth)&&n<capacity)output[n++]={child.first,0,child.second,0,0};return n;
}
API int Core_SetOwner(const wchar_t* sid){PSID parsed=nullptr;if(!ConvertStringSidToSidW(sid,&parsed))return 0;DWORD size=GetLengthSid(parsed);std::unique_lock lk(stateMutex);ownerSid.assign((uint8_t*)parsed,(uint8_t*)parsed+size);LocalFree(parsed);return 1;}
API int Core_RouteForTest(uint32_t pid,uint64_t birth,int ipv6,uint32_t* proxy) {RouteRule r{};int action=policyAction(pid,birth,ipv6!=0,r);if(proxy)*proxy=r.proxy;return action;}
API int Core_RoutePacketForTest(uint32_t pid,uint64_t birth,int ipv6,uint16_t localPort,uint32_t* proxy) {RouteRule r{};int action=policyAction(pid,birth,ipv6!=0,r,localPort);if(proxy)*proxy=r.proxy;return action;}
API void Core_ClearGuards(){std::unique_lock lk(stateMutex);guards.clear();}
API void Core_AddGuard(const wchar_t* path){if(!path)return;std::unique_lock lk(stateMutex);for(const auto& g:guards)if(_wcsicmp(g.c_str(),path)==0)return;guards.emplace_back(path);}
API void Core_SetGuards(const wchar_t* paths){std::vector<std::wstring> next;if(paths){for(auto p=paths;*p;p+=wcslen(p)+1)next.emplace_back(p);}std::unique_lock lk(stateMutex);guards.swap(next);}
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
    lastError=0;WSADATA ws{};int startup=WSAStartup(MAKEWORD(2,2),&ws);if(startup){lastError=startup;return 0;}
    auto failed=[&](){if(listener!=INVALID_SOCKET){closesocket(listener);listener=INVALID_SOCKET;}if(driverLibrary){FreeLibrary(driverLibrary);driverLibrary=nullptr;}WSACleanup();return 0;};
    driverLibrary=LoadLibraryExW(libraryPath,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if(!driverLibrary){lastError=GetLastError();return failed();}
#define LOAD(name) wd##name=(decltype(wd##name))GetProcAddress(driverLibrary,"WinDivert" #name);if(!wd##name){lastError=ERROR_PROC_NOT_FOUND;return failed();}
    LOAD(Open) LOAD(Recv) LOAD(Send) LOAD(Close) LOAD(Shutdown)
#undef LOAD
    wdChecksums=(decltype(wdChecksums))GetProcAddress(driverLibrary,"WinDivertHelperCalcChecksums");if(!wdChecksums){lastError=ERROR_PROC_NOT_FOUND;return failed();}
    listener=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);sockaddr_in local{};local.sin_family=AF_INET;local.sin_addr.s_addr=INADDR_ANY;
    if(bind(listener,(sockaddr*)&local,sizeof(local))||listen(listener,SOMAXCONN)){lastError=WSAGetLastError();return failed();}
    int len=sizeof(local);getsockname(listener,(sockaddr*)&local,&len);listenPort=ntohs(local.sin_port);
    divert=wdOpen("outbound and (ip or ipv6)",WINDIVERT_LAYER_NETWORK,100,0);
    if(divert==INVALID_HANDLE_VALUE){lastError=GetLastError();return failed();}
    active=true;captureThread=std::thread([](){try{captureLoop();}catch(...){lastError=ERROR_UNHANDLED_EXCEPTION;}});acceptThread=std::thread(acceptLoop);return 1;
}
API void Core_Stop() {
    std::lock_guard lifecycle(lifecycleMutex);if(!active.exchange(false))return;
    closesocket(listener);listener=INVALID_SOCKET;
    {std::lock_guard lk(flowMutex);for(auto& [k,f]:flows)f->close(EngineStopped);}
    wdShutdown(divert,WINDIVERT_SHUTDOWN_RECV);
    if(captureThread.joinable())captureThread.join();if(acceptThread.joinable())acceptThread.join();
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
