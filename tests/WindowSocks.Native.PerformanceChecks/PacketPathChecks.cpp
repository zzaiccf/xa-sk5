// Exercises the production packet loop using the official parser/checksummer and
// fake receive/send functions. Never opens a WinDivert handle or changes routing.
#include <cstdio>
#include <stdexcept>
#include <future>
#include "../../native/WindowSocksNative.cpp"
#include <tlhelp32.h>

static unsigned checks=0,checksumCalls=0,sendCalls=0,batchSends=0,received=0,delivered=0;
static unsigned totalPackets=0;
static bool firstUnknown=false;
static bool malformedFirst=false;
static DWORD singleSendError=0,batchSendError=0;
static std::vector<uint8_t> fixture;
static WINDIVERT_ADDRESS lastAddress{};
static std::vector<uint8_t> lastPacket;
static decltype(wdChecksums) officialChecksums;
static void require(bool value,const char* message){++checks;if(!value)throw std::runtime_error(message);}
static BOOL fakeChecksums(void* p,UINT n,WINDIVERT_ADDRESS* a,UINT64 flags){++checksumCalls;return officialChecksums(p,n,a,flags);}
static BOOL fakeSend(HANDLE,const void* p,UINT n,UINT*,const WINDIVERT_ADDRESS* a){++sendCalls;lastPacket.assign((const uint8_t*)p,(const uint8_t*)p+n);lastAddress=*a;if(singleSendError){SetLastError(singleSendError);return FALSE;}return TRUE;}
static BOOL fakeShutdown(HANDLE,WINDIVERT_SHUTDOWN){return TRUE;}
static BOOL fakeClose(HANDLE){return TRUE;}
static BOOL fakeSendEx(HANDLE,const void* p,UINT n,UINT*,UINT64,const WINDIVERT_ADDRESS* a,UINT addrLength,OVERLAPPED*){
    ++batchSends;delivered+=addrLength/sizeof(WINDIVERT_ADDRESS);
    require(n==fixture.size()*(addrLength/sizeof(WINDIVERT_ADDRESS)),"batch lengths/addresses must match");
    for(UINT i=0;i<addrLength/sizeof(WINDIVERT_ADDRESS);i++){
        require(a[i].Timestamp==delivered-addrLength/sizeof(WINDIVERT_ADDRESS)+i+(firstUnknown?1:0),"packet/address order must be preserved");
        require(memcmp((const uint8_t*)p+i*fixture.size(),fixture.data(),fixture.size())==0,"direct packet must be unchanged");
    }
    if(batchSendError){DWORD error=batchSendError;batchSendError=0;SetLastError(error);return FALSE;}
    return TRUE;
}
static BOOL fakeRecvEx(HANDLE,void* p,UINT capacity,UINT* n,UINT64,WINDIVERT_ADDRESS* a,UINT* addrLength,OVERLAPPED*){
    if(received>=totalPackets){active=false;return FALSE;}
    UINT count=(std::min)({64u,totalPackets-received,capacity/(UINT)fixture.size(),*addrLength/(UINT)sizeof(WINDIVERT_ADDRESS)});
    for(UINT i=0;i<count;i++){
        memcpy((uint8_t*)p+i*fixture.size(),fixture.data(),fixture.size());
        if(firstUnknown&&received+i==0)put16((uint8_t*)p+i*fixture.size()+20,0);
        if(malformedFirst&&received+i==0)((uint8_t*)p)[i*fixture.size()]=0x40;
        a[i]={};a[i].Outbound=1;a[i].IPChecksum=a[i].TCPChecksum=a[i].UDPChecksum=1;a[i].Timestamp=received+i;
    }
    received+=count;*n=count*(UINT)fixture.size();*addrLength=count*sizeof(WINDIVERT_ADDRESS);return TRUE;
}
static std::vector<uint8_t> packet(bool udp=false,bool ipv6=false){
    std::vector<uint8_t> p(ipv6?60:40);p[0]=ipv6?0x60:0x45;
    if(ipv6){put16(p.data()+4,20);p[6]=udp?17:6;p[7]=64;p[8]=0x20;p[24]=0x20;p[39]=1;}
    else{put16(p.data()+2,40);p[8]=64;p[9]=udp?17:6;uint32_t src=inet_addr("192.0.2.1"),dst=inet_addr("198.51.100.1");memcpy(p.data()+12,&src,4);memcpy(p.data()+16,&dst,4);}
    auto off=ipv6?40:20;put16(p.data()+off,41001);put16(p.data()+off+2,443);
    if(udp)put16(p.data()+off+4,20);else{p[off+12]=0x50;p[off+13]=2;}
    return p;
}
static void reset(){Core_ReplacePolicy(nullptr,0,L"\0");received=delivered=batchSends=sendCalls=checksumCalls=0;tcpOwners.clear();firstUnknown=false;singleSendError=batchSendError=0;lastError=0;captureFailed=false;sandboxIdentityState=0;sandboxIdentityCache.clear();sandboxIdentityOrder.clear();retiredTuples.clear();retiredAliases.clear();retiredTupleCount=0;}
static uint32_t parentPid(){
    uint32_t parent=0;HANDLE snap=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);PROCESSENTRY32W e{};e.dwSize=sizeof(e);
    if(Process32FirstW(snap,&e))do{if(e.th32ProcessID==GetCurrentProcessId())parent=e.th32ParentProcessID;}while(Process32NextW(snap,&e));CloseHandle(snap);return parent;
}
static OwnerBatch tcpOwner(uint32_t pid,const std::vector<uint8_t>& p){
    OwnerBatch b;auto& table=b.tables[0];table.attempted=true;table.at=GetTickCount64();table.data.resize(sizeof(MIB_TCPTABLE_OWNER_PID));
    auto* t=(MIB_TCPTABLE_OWNER_PID*)table.data.data();t->dwNumEntries=1;auto& r=t->table[0];
    r.dwLocalAddr=word(p.data()+12);r.dwRemoteAddr=word(p.data()+16);r.dwLocalPort=htons(be16(p.data()+20));r.dwRemotePort=htons(be16(p.data()+22));r.dwOwningPid=pid;
    return b;
}
static void batchLoopChecks(){
    for(bool ipv6:{false,true})for(bool udp:{false,true}){
        reset();fixture=packet(udp,ipv6);totalPackets=1024;active=true;captureLoop();
        require(delivered==1024,"every direct packet must be sent");require(batchSends==16,"64 direct packets must use one send operation");
        require(checksumCalls==0,"valid unchanged checksums must not be recalculated");
    }
    reset();RouteRule direct{123456,0,42,2,0};Core_ReplaceRules(&direct,1);
    auto p=packet(true);OwnerBatch b;PassThroughBatch out;WINDIVERT_ADDRESS addr{};addr.IPChecksum=addr.UDPChecksum=1;
    require(processPacket(p,addr,b,&out),"inventory-only rules pass directly");require(b.queries==0,"inventory must not enumerate socket tables");
    require(out.addresses.size()==1,"inventory traffic stays direct");
    addr.UDPChecksum=0;require(processPacket(p,addr,b,&out),"offloaded packet still passes");
    require(checksumCalls==1,"offloaded/invalid checksums must still be completed");
}
static void sendFailureChecks(){
    reset();active=true;auto p=packet();WINDIVERT_ADDRESS addr{};addr.IPChecksum=addr.TCPChecksum=1;
    auto retained=std::make_shared<Flow>();retained->key={inet_addr("192.0.2.10"),inet_addr("198.51.100.10"),40000,443,6};retained->connected=true;
    flows[retained->key]=retained;
    singleSendError=ERROR_HOST_UNREACHABLE;
    require(!inject(p,addr,false),"a rejected packet must report injection failure");
    require(Core_IsRunning()!=0&&retained->alive,"a packet-local 1232 must not restart the engine or close unrelated relays");
    singleSendError=0;require(inject(p,addr,false),"later packets must still inject after 1232");
    auto droppedBefore=packetMetrics.expiredImpostors.load();
    for(bool ipv6:{false,true})for(uint8_t hop:{0,1,2}){
        auto expired=packet(false,ipv6);expired[ipv6?7:8]=hop;addr.Impostor=1;unsigned before=sendCalls;
        require(inject(expired,addr,false)==(hop>1),"impostor loop protection must reject only exhausted TTL/HopLimit");
        require(sendCalls==before+(hop>1?1:0),"expired impostors must not enter the driver");
        PassThroughBatch filtered;filtered.append(expired,addr);
        require(filtered.addresses.size()==(hop>1?1:0),"expired impostors must not poison a pass-through batch");
        addr.Impostor=0;require(inject(expired,addr,false),"ordinary low-TTL packets must retain Windows routing behavior");
    }
    require(packetMetrics.expiredImpostors==droppedBefore+8,"expired-impostor diagnostics must count both injection paths");
    singleSendError=ERROR_INVALID_HANDLE;require(!inject(p,addr,false)&&Core_IsRunning()==0,"invalid capture handles must remain fatal");
    flows.clear();retained.reset();reset();fixture=packet();totalPackets=192;active=true;batchSendError=ERROR_HOST_UNREACHABLE;captureLoop();
    require(received==192&&batchSends==3&&lastError==0,"batch 1232 must allow later receive batches without re-sending the rejected batch");
    reset();fixture=packet();totalPackets=192;active=true;batchSendError=ERROR_INVALID_HANDLE;captureLoop();
    require(received==64&&batchSends==1&&lastError==ERROR_INVALID_HANDLE,"fatal batch errors must still stop capture");
    reset();active=false;
}
static void policyChecks(uint32_t pid,uint64_t birth){
    reset();Core_SetProxy(910,"127.0.0.1",1080,"","");RouteRule rule{pid,910,birth,1,0};Core_ReplacePolicy(&rule,1,L"\0");
    auto p=packet();WINDIVERT_ADDRESS addr{};addr.IPChecksum=addr.TCPChecksum=1;PassThroughBatch out;auto ownerTable=tcpOwner(pid,p);
    require(processPacket(p,addr,ownerTable,&out),"known blocked owner is classified");
    require(out.addresses.empty()&&sendCalls==0,"paused rules never become direct traffic");
    rule.blocked=0;Core_ReplacePolicy(&rule,1,L"\0");ownerTable=tcpOwner(pid,p);
    require(processPacket(p,addr,ownerTable,&out),"proxy SYN is classified");
    require(sendCalls==1&&checksumCalls==1,"rewritten SYN must be injected with fresh checksums");
    require(lastAddress.Outbound==0&&be16(lastPacket.data()+22)==listenPort,"proxy SYN must target the local relay");
    require(!flows.empty()&&flows.begin()->second->alive,"a routed flow must exist");
    rule.blocked=1;Core_ReplacePolicy(&rule,1,L"\0");require(!flows.begin()->second->alive,"pause must close an existing proxy flow immediately");
    {std::lock_guard lk(flowMutex);flows.clear();aliases.clear();}
    Core_ReplacePolicy(nullptr,0,L"Ld9BoxHeadless.exe\0");RouteRule found{};
    require(policyAction(pid,birth,false,found)==0,"unrelated executable stays direct");require(!guardResults.empty(),"guard decision must be cached");
    auto count=guardResults.size();Core_ReplacePolicy(nullptr,0,L"Ld9BoxHeadless.exe\0");require(guardResults.size()==count,"identical monitor policies must preserve classification cache");
    wchar_t path[32768];DWORD n=32768;HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
    require(process&&QueryFullProcessImageNameW(process,0,path,&n),"parent image must be readable");CloseHandle(process);
    Core_SetGuards((std::wstring(path)+L'\0'+L'\0').c_str());
    require(policyAction(pid,birth,false,found)==2,"changing guards must invalidate a cached direct result");
    Core_ClearGuards();require(policyAction(pid,birth,false,found)==0,"clearing guards restores direct traffic");
    // Simulated ambiguous owners retain the existing fail-closed policy.
    OwnerBatch ambiguous;auto& table=ambiguous.tables[1];table.attempted=true;table.at=GetTickCount64();table.data.resize(sizeof(MIB_UDPTABLE_OWNER_MODULE)+sizeof(MIB_UDPROW_OWNER_MODULE));
    auto* t=(MIB_UDPTABLE_OWNER_MODULE*)table.data.data();t->dwNumEntries=2;
    for(unsigned i=0;i<2;i++){t->table[i]={};t->table[i].dwLocalPort=htons(41001);t->table[i].dwOwningPid=pid;t->table[i].liCreateTimestamp.QuadPart=birth+i;}
    uint64_t socketBirth=0;Key key{inet_addr("192.0.2.1"),inet_addr("198.51.100.1"),41001,443,17};
    require(owner(key,socketBirth,false,&ambiguous)==UINT32_MAX,"same-port sockets with different births must remain ambiguous");
}
static void concurrentActivityChecks(){
    reset();
    auto add=[](uint8_t protocol,uint16_t port,uint64_t last,bool alive=true,bool gateway=true){
        auto f=std::make_shared<Flow>();f->key={inet_addr("192.0.2.1"),inet_addr("198.51.100.1"),port,443,protocol};
        f->gatewayInterface=gateway?1:0;f->last=last;f->alias=port;if(!alive){f->close();f->closedAt=last;quarantine(f->key,f->alias,last);}
        flows[f->key]=f;aliases[f->alias]=f;return f;
    };
    // Model an activity update arriving after prune sampled its clock. Future
    // timestamps make this real cleanup-path regression deterministic.
    const uint64_t now=3600000;
    auto tcp=add(6,42001,now+60000),udp=add(17,42002,now+60000);
    auto closed=add(6,42003,now+60000,false);
    auto opening=add(6,42004,now,true,false);opening->opened=now+60000;
    prune(now);
    require(tcp->alive&&flows.count(tcp->key)&&aliases.count(tcp->alias),"concurrent TCP activity must not underflow into idle expiration");
    require(udp->alive&&flows.count(udp->key),"concurrent UDP activity must not underflow into idle expiration");
    require(retiredTuples.count(closed->key),"recently closed flow must retain its grace period despite a newer activity timestamp");
    require(opening->alive,"a flow opened after the cleanup clock sample must retain its socket discovery grace period");
    tcp->last=now;udp->last=now;closed->last=now;opening->gatewayInterface=1;
    prune(now);require(flows.size()==3,"recent activity and closed-flow grace periods must be retained");
    tcp->last=now-1800000;udp->last=now-60000;closed->last=now-240000;
    prune(now);require(flows.size()==3,"cleanup must honor the exact idle and closed-grace boundaries");
    prune(now+1);
    require(!tcp->alive&&!udp->alive&&flows.size()==1&&flows.count(opening->key),"truly idle TCP/UDP and expired closed flows must still be removed");
    require(aliases.size()==1&&aliases.count(opening->alias),"cleanup must release only expired flow aliases");
    flows.clear();aliases.clear();
}
static void socketReuseChecks(){
    reset();sockaddr_in a{};a.sin_family=AF_INET;a.sin_addr.s_addr=inet_addr("127.0.0.1");int n=sizeof(a);
    SOCKET s=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);require(bind(s,(sockaddr*)&a,n)==0,"UDP fixture must bind");getsockname(s,(sockaddr*)&a,&n);
    Key key{a.sin_addr.s_addr,inet_addr("198.51.100.1"),ntohs(a.sin_port),443,17};OwnerBatch first;uint64_t oldBirth=0;
    require(owner(key,oldBirth,false,&first)==GetCurrentProcessId()&&oldBirth,"first socket must be identified");
    closesocket(s);Sleep(20);s=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);require(bind(s,(sockaddr*)&a,n)==0,"UDP port must be reusable");
    OwnerBatch second;uint64_t newBirth=0;require(owner(key,newBirth,false,&second)==GetCurrentProcessId()&&newBirth!=oldBirth,"next batch must identify the replacement socket's creation time");
    require(first.queries==1&&second.queries==1,"ownership snapshots must be scoped to each batch");closesocket(s);
}
static void followedTreeChecks(uint32_t pid,uint64_t birth){
    reset();require(parentOf(GetCurrentProcessId(),created(GetCurrentProcessId()))==pid,"direct parent query must agree with an independent Toolhelp snapshot");
    require(parentOf(pid,birth+1)==0,"parent query must reject a recycled process identity");
    RouteRule root{123456789,910,42,1,1};Core_ReplacePolicy(&root,1,L"\0");RouteRule found{};
    require(policyAction(pid,birth,false,found)==0,"unrelated traffic stays direct under a followed process rule");
    // Invalidate the one-second negative result on every call. This exercises the
    // real ancestry fallback, which the old inventory-only benchmark never did.
    double longest=0;auto start=std::chrono::steady_clock::now();
    for(int i=0;i<100;i++){
        unrelatedProcesses.clear();auto one=std::chrono::steady_clock::now();
        require(policyAction(pid,birth,false,found)==0,"repeated ancestry lookup must preserve unrelated routing");
        longest=(std::max)(longest,std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-one).count());
    }
    auto ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count();
    require(ms<1000,"100 uncached ancestry lookups must not monopolize the packet thread for seconds");
    printf("followed_tree_uncached: 100 lookups, %.3f ms total, %.3f ms max\n",ms,longest);
    // A real child must inherit even when Windows job assignment is unavailable.
    // Set the test rule without creating a job, then use an independent Win32 child.
    rules={{GetCurrentProcessId(),910,created(GetCurrentProcessId()),1,1}};unrelatedProcesses.clear();
    wchar_t system[MAX_PATH];GetSystemDirectoryW(system,MAX_PATH);std::wstring executable=std::wstring(system)+L"\\ping.exe";
    std::wstring command=L"\""+executable+L"\" -n 60 127.0.0.1";STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};
    require(CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&si,&pi)!=0,"live child fixture must launch");
    uint64_t childBirth=created(pi.dwProcessId);int inherited=policyAction(pi.dwProcessId,childBirth,false,found);
    TerminateProcess(pi.hProcess,0);WaitForSingleObject(pi.hProcess,5000);CloseHandle(pi.hThread);CloseHandle(pi.hProcess);
    require(childBirth&&inherited==2&&found.proxy==910,"real child must inherit the paused parent through point queries without a job");
    reset();
}
static void retryChecks(){
    reset();RouteRule rule{123456,0,42,1,0};Core_ReplaceRules(&rule,1);
    auto p=packet(true);put16(p.data()+20,0);WINDIVERT_ADDRESS addr{};OwnerBatch b;PassThroughBatch out;
    require(!processPacket(p,addr,b,&out),"unknown owner must be deferred, never passed directly");require(out.addresses.empty(),"unknown owner must not bypass a managed rule");
    for(int i=0;i<300;i++)deferPacket(p,addr);require(deferredPackets.size()==256,"retry queue must be bounded");
    active=true;std::thread worker(retryLoop);Sleep(5);auto start=std::chrono::steady_clock::now();active=false;retryWake.notify_all();worker.join();
    require(std::chrono::steady_clock::now()-start<std::chrono::seconds(1),"stop must interrupt retries promptly");
    {std::lock_guard lk(retryMutex);deferredPackets.clear();}
    // One unresolved datagram in a busy receive batch must not hold the 63 known
    // packets behind its retry timer. Use a real wildcard-bound fixture socket.
    reset();Core_ReplaceRules(&rule,1);SOCKET s=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);sockaddr_in a{};a.sin_family=AF_INET;
    require(bind(s,(sockaddr*)&a,sizeof(a))==0,"mixed batch fixture must bind");int size=sizeof(a);getsockname(s,(sockaddr*)&a,&size);
    fixture=packet(true);put16(fixture.data()+20,ntohs(a.sin_port));firstUnknown=true;totalPackets=64;active=true;captureLoop();
    require(delivered==63&&batchSends==1,"unknown ownership must not delay forwarding the known packets in its batch");
    require(deferredPackets.size()==1,"only the unresolved packet should enter the retry queue");
    {std::lock_guard lk(retryMutex);deferredPackets.clear();}closesocket(s);
}
static void reportBenchmark(){
    reset();fixture=packet(true);totalPackets=64000;auto start=std::chrono::steady_clock::now();active=true;captureLoop();
    auto elapsed=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-start).count();
    printf("inventory_packet_loop: %u packets, %u receive/send batches, %.3f ms (driver calls simulated)\n",delivered,batchSends,elapsed);
    sockaddr_in a{};a.sin_family=AF_INET;a.sin_addr.s_addr=inet_addr("127.0.0.1");SOCKET s=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);bind(s,(sockaddr*)&a,sizeof(a));int size=sizeof(a);getsockname(s,(sockaddr*)&a,&size);
    Key key{a.sin_addr.s_addr,inet_addr("198.51.100.1"),ntohs(a.sin_port),443,17};
    start=std::chrono::steady_clock::now();unsigned queries=0;
    for(int i=0;i<300;i++){OwnerBatch b;for(int j=0;j<64;j++){uint64_t birth=0;require(owner(key,birth,false,&b)==GetCurrentProcessId(),"batched UDP lookup must retain correct ownership");}queries+=b.queries;}
    auto us=std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count()/19200;
    printf("udp_batched_owner: %.3f us/packet, %u snapshots/19200 lookups\n",us,queries);closesocket(s);
}
static void shutdownChecks(){
    reset();wdShutdown=fakeShutdown;wdClose=fakeClose;DWORD before=0,after=0;GetProcessHandleCount(GetCurrentProcess(),&before);
    for(int i=0;i<10;i++){
        // Core_Stop owns the actual production retry/prune threads. The only
        // replaced components are the driver syscalls; no driver is installed.
        WSADATA ws;WSAStartup(MAKEWORD(2,2),&ws);divert=(HANDLE)1;active=true;
        retryThread=std::thread(retryLoop);pruneThread=std::thread(pruneLoop);
        auto start=std::chrono::steady_clock::now();Core_Stop();
        require(std::chrono::steady_clock::now()-start<std::chrono::seconds(1),"Core_Stop must wake both helper threads promptly");
        require(!active&&!retryThread.joinable()&&!pruneThread.joinable()&&deferredPackets.empty(),"stop must release all helper threads and queued packets");
    }
    GetProcessHandleCount(GetCurrentProcess(),&after);require(after<=before+2,"repeated start/stop must not leak thread/event handles");
}
static void telemetryChecks(){
    startTelemetry();require(telemetryPath.empty(),"ordinary applications/tests must not start persistent service telemetry");
    wchar_t temp[MAX_PATH];GetTempPathW(MAX_PATH,temp);std::wstring directory=std::wstring(temp)+L"WindowSocksNative-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64());
    require(CreateDirectoryW(directory.c_str(),nullptr)!=0,"isolated telemetry directory must be created");
    auto write=[&](const std::wstring& name){FILE* file=nullptr;_wfopen_s(&file,(directory+L"\\"+name).c_str(),L"wb");require(file!=nullptr,"telemetry fixture must open");fputs("fixture",file);fclose(file);};
    for(unsigned i=0;i<18;i++)write(L"packet-history-"+std::to_wstring(i)+L".jsonl");write(L"user-document.txt");
    telemetryDirectory=directory;telemetryPath=directory+L"\\packet-overflow.jsonl";FILE* large=nullptr;_wfopen_s(&large,telemetryPath.c_str(),L"wb");require(large!=nullptr,"overflow fixture must open");_fseeki64(large,8*1024*1024+1,SEEK_SET);fputc('x',large);fclose(large);
    saveTelemetry();require(telemetryPath.find(L"packet-overflow.jsonl")==std::wstring::npos,"full telemetry log must rotate instead of stopping records");
    WIN32_FIND_DATAW entry{};HANDLE find=FindFirstFileW((directory+L"\\packet-*.jsonl").c_str(),&entry);unsigned count=0;
    if(find!=INVALID_HANDLE_VALUE){do{++count;}while(FindNextFileW(find,&entry));FindClose(find);}
    require(count==16,"rotation must retain only sixteen generated segments");require(GetFileAttributesW((directory+L"\\user-document.txt").c_str())!=INVALID_FILE_ATTRIBUTES,"rotation must preserve unrelated documents");
    FILE* file=nullptr;_wfopen_s(&file,telemetryPath.c_str(),L"rb");require(file!=nullptr,"new telemetry segment must contain a record");char line[4096]{};bool written=fgets(line,sizeof(line),file)!=nullptr;
    require(written&&strstr(line,"\"native\":\"" XA_VERSION "\"")&&strstr(line,"\"ageMaxUs\""),"rotated record must include version and queue timing");
    require(fgets(line,sizeof(line),file)!=nullptr&&strstr(line,"\"detail\":true"),"packet detail record must remain available");
    require(fgets(line,sizeof(line),file)!=nullptr&&strstr(line,"\"relay\":true")&&strstr(line,"\"tcpQueueMaxUs\"")&&strstr(line,"\"tcpWouldBlock\"")&&strstr(line,"\"tcpStallTimeouts\""),"relay waiting and timeout counters must be persisted for remote diagnostics");
    fclose(file);
    telemetryPath.clear();telemetryDirectory.clear();
    // Delete only files in this exact directory created above; no recursive move/delete.
    find=FindFirstFileW((directory+L"\\*").c_str(),&entry);if(find!=INVALID_HANDLE_VALUE){do{if(!(entry.dwFileAttributes&FILE_ATTRIBUTE_DIRECTORY))DeleteFileW((directory+L"\\"+entry.cFileName).c_str());}while(FindNextFileW(find,&entry));FindClose(find);}require(RemoveDirectoryW(directory.c_str())!=0,"isolated telemetry files must be cleaned up");
}
static void p05Checks(){
    reset(); auto p=packet();p[33]=0x11;
    auto f=std::make_shared<Flow>();f->key={word(p.data()+12),word(p.data()+16),41001,443,6};
    f->rule={UINT32_MAX-1,910,42,0,0};f->alias=42000;f->close();
    flows[f->key]=f;aliases[f->alias]=f;
    RouteRule r=f->rule;Core_ReplacePolicy(&r,1,L"\0");
    WINDIVERT_ADDRESS a{};a.IPChecksum=a.TCPChecksum=1;PassThroughBatch out;auto owners=tcpOwner(r.pid,p);
    require(processPacket(p,a,owners,&out),"P0-5 exited TCP tail must be classified");
    require(out.addresses.empty()&&sendCalls==0,"P0-5 exited managed FIN must never escape directly");
    p[33]=4;owners=tcpOwner(r.pid,p);require(processPacket(p,a,owners,&out)&&out.addresses.empty(),"P0-5 exited RST must never escape directly");
    flows.clear();aliases.clear();reset();p=packet();put16(p.data()+20,listenPort);owners=tcpOwner(GetCurrentProcessId(),p);
    require(processPacket(p,a,owners,&out)&&out.addresses.empty()&&sendCalls==0,"P0-5 orphan relay packet must be dropped");
    retiredListenerPorts[17000]=GetTickCount64();put16(p.data()+20,17000);owners=tcpOwner(GetCurrentProcessId(),p);
    require(processPacket(p,a,owners,&out)&&out.addresses.empty()&&sendCalls==0,"P0-5 retired listener port must never regain the service direct exemption");retiredListenerPorts.clear();
    reset();RouteRule prior{UINT32_MAX-1,910,42,1,0},found{};Core_ReplacePolicy(&prior,1,L"unrelated.exe\0");
    require(policyAction(prior.pid,0,false,found)==2,"P0-5 unknown birth for a managed PID must fail closed");
    reset();f->last=1000;f->closedAt=1000;quarantine(f->key,f->alias,1000);flows[f->key]=f;aliases[f->alias]=f;prune(7000);
    require(retiredAliases.count(f->alias),"P0-5 relay alias must remain quarantined for TCP TIME_WAIT");
    prune(241001);require(!retiredAliases.count(f->alias),"P0-5 alias quarantine must eventually expire");flows.clear();aliases.clear();reset();
}
static void p06Checks(uint32_t pid,uint64_t birth){
    reset();Core_SetProxy(910,"127.0.0.1",1080,"","");RouteRule exemption{pid,0,birth,3,0},found{};
    Core_ReplacePolicyEx2(&exemption,1,L"\0",910,0,0,0,L"\0",0,0,0);require(policyAction(pid,birth,false,found)==0,"P0-6 guard exemption is direct when global is off");
    Core_ReplacePolicyEx2(&exemption,1,L"\0",910,1,0,0,L"\0",0,0,0);require(policyAction(pid,birth,false,found)==1&&found.proxy==910,"P0-6 guard exemption must use the global proxy");
    require(policyAction(pid,birth,true,found)==2,"P0-6 exempt IPv6 must retain global blocking");
    Core_ReplacePolicyEx2(&exemption,1,L"\0",910,2,0,0,L"\0",0,0,0);require(policyAction(pid,birth,false,found)==2,"P0-6 unhealthy global must block exempt targets");reset();
}
static void p04Checks(){
    reset();active=true;lastError=ERROR_INVALID_DATA;
    require(Core_IsRunning()!=0,"P0-4 diagnostic parser error must not mark healthy capture as stopped");
    lastError=0;singleSendError=ERROR_INVALID_PARAMETER;auto p=packet();WINDIVERT_ADDRESS a{};
    require(!inject(p,a,false)&&Core_IsRunning()!=0,"P0-4 packet rejection must not tear down all flows");
    gatewayError=ERROR_ACCESS_DENIED;require(Core_IsRunning()!=0,"P0-4 gateway errors must not mark host capture failed");gatewayError=0;
    reset();fixture=packet();totalPackets=128;firstUnknown=true;malformedFirst=true;active=true;auto before=packetMetrics.parseErrors.load();captureLoop();
    require(received==128&&delivered==127&&packetMetrics.parseErrors==before+1&&!captureFailed,"P0-4 malformed packet must not discard later valid packets in its batch");
    malformedFirst=false;reset();active=false;
}
static void p01Checks(uint32_t pid,uint64_t birth){
    reset();Core_SetGuards(L"@box:Xa_0123456789abcdef01234567\0");
    sandboxIdentityQuery=[](uint32_t,uint64_t,std::wstring& box){box=L"Xa_0123456789abcdef01234567";return 1;};
    RouteRule found{};
    require(policyAction(pid,birth,false,found)==2,"P0-1 service-created sandbox member must block before discovery even outside game directory");
    require(policyAction(pid,birth,true,found)==2,"P0-1 undiscovered sandbox IPv6 must block");
    sandboxIdentityQuery=[](uint32_t,uint64_t,std::wstring& box){box.clear();return 0;};
    require(policyAction(pid,birth+1,false,found)==0,"P0-1 outside same-image process must stay direct");
    sandboxIdentityQuery={};reset();
}
static void r3Checks(uint32_t pid,uint64_t birth){
    reset();Core_SetGuards(L"@box:Xa_0123456789abcdef01234567\0");
    sandboxIdentityState=1;sandboxIdentityCache.clear();sandboxIdentityQuery=[](uint32_t,uint64_t,std::wstring&){return -1;};
    RouteRule found{};
    require(policyAction(pid,birth,false,found)==0,"R3 missing or incompatible identity driver must not block unrelated host traffic");
    require(sandboxIdentityState==1,"N1 a single PID error must not change global driver health");
    auto key=std::make_pair(pid,birth);int attempts=0;
    sandboxIdentityQuery=[&](uint32_t,uint64_t,std::wstring&){++attempts;return 0;};
    pendingSandbox(pid,birth);require(attempts==0,"N1 unknown PID retry must back off");
    sandboxIdentityCache[key].checked=GetTickCount64()-500;
    pendingSandbox(pid,birth);require(attempts==1,"N1 unknown PID retries after 500ms");
    sandboxDriverFailures=0;recordSandboxDriverHealth(-1,1000);require(sandboxIdentityState==1,"N1 first driver failure keeps established routes");
    recordSandboxDriverHealth(-1,6000);require(sandboxIdentityState==1,"N1 second driver failure keeps established routes");
    recordSandboxDriverHealth(-1,11000);require(sandboxIdentityState==-1,"N1 third driver failure pauses managed routes");
    recordSandboxDriverHealth(1,16000);require(sandboxIdentityState==1&&sandboxDriverFailures==0,"N1 driver recovery automatically resumes existing identities");
    recordSandboxDriverHealth(-1,20000);recordSandboxDriverHealth(-1,35000);require(sandboxIdentityState==-1,"N1 15 second outage also reaches threshold");
    recordSandboxDriverHealth(1,40000);
    sandboxIdentityCache.clear();sandboxIdentityQuery=[](uint32_t,uint64_t,std::wstring&){return -2;};
    pendingSandbox(pid,birth);require(sandboxIdentityState==1&&sandboxDriverCheckRequested,"N1 packet path requests control-thread driver check without changing global health");
    sandboxIdentityState=-1;
    RouteRule boxed{pid,910,birth,0,8};Core_SetProxy(910,"127.0.0.1",1080,"","");Core_ReplacePolicy(&boxed,1,L"\0");
    require(policyAction(pid,birth,false,found)==2,"R3 verified sandbox members must block immediately on identity failure");
    Core_ReplacePolicy(nullptr,0,L"@box:Xa_0123456789abcdef01234567\0");sandboxIdentityCache.clear();sandboxIdentityOrder.clear();
    sandboxIdentityQuery=[](uint32_t,uint64_t,std::wstring&){return 0;};
    for(uint32_t i=1;i<=5000;i++)pendingSandbox(i,100);
    require(sandboxIdentityCache.size()==4096,"R3 cache pressure must evict incrementally, not discard all confirmed host identities");
    sandboxIdentityQuery={};reset();
}
static void r5Checks(){
    reset();Core_SetGuards(L"unrelated.exe\0");RouteRule found{};
    require(policyAction(4,0,false,found)==0,"R5 System without a managed identity must not be blocked by unrelated guards");
    require(policyAction(UINT32_MAX-1,0,false,found)==0,"R5 an unmanaged PPL/unknown-birth process must keep its existing policy");
    RouteRule known{UINT32_MAX-1,910,123,1,0};Core_ReplacePolicy(&known,1,L"unrelated.exe\0");
    require(policyAction(known.pid,0,false,found)==2,"R5 a previously managed PID with unknown birth must remain blocked");
    reset();
}
static std::vector<std::string> recoverySteps;
static bool recoveryOpenFails=false;
static HANDLE recoveryOpen(const char*,WINDIVERT_LAYER,INT16 priority,UINT64 flags){
    recoverySteps.push_back("open");require(priority!=capturePriority&&flags==0,"R8 replacement must intercept at a distinct priority");
    if(recoveryOpenFails){SetLastError(ERROR_ACCESS_DENIED);return INVALID_HANDLE_VALUE;}
    return (HANDLE)(uintptr_t)0x9876;
}
static BOOL recoveryShutdown(HANDLE,WINDIVERT_SHUTDOWN){recoverySteps.push_back("shutdown");return TRUE;}
static BOOL recoveryClose(HANDLE){recoverySteps.push_back("close");return TRUE;}
static void r8Checks(){
    reset();auto savedOpen=wdOpen;auto savedShutdown=wdShutdown;auto savedClose=wdClose;auto savedDivert=divert;
    wdOpen=recoveryOpen;wdShutdown=recoveryShutdown;wdClose=recoveryClose;
    active=true;captureFailed=false;recoverySteps.clear();
    require(Core_ReplaceCapture()==1&&recoverySteps.empty(),"R8 healthy capture must not restart");
    captureFailed=true;recoveryOpenFails=true;
    require(Core_ReplaceCapture()==0&&recoverySteps==std::vector<std::string>{"open"},"R8 failed replacement must not close the old handle");
    recoveryOpenFails=false;recoverySteps.clear();totalPackets=0;
    require(Core_ReplaceCapture()==1,"R8 capture replacement must start");
    if(captureThread.joinable())captureThread.join();
    require(recoverySteps==std::vector<std::string>{"open","shutdown","close"},"R8 new capture must exist before old capture stops");
    active=false;divert=savedDivert;wdOpen=savedOpen;wdShutdown=savedShutdown;wdClose=savedClose;reset();
}
static void r4Checks(){
    reset();flows.clear();aliases.clear();
    for(unsigned i=0;i<20000;i++){
        auto f=std::make_shared<Flow>();f->key={htonl(i+1),inet_addr("198.51.100.88"),40000,443,6};
        f->close();flows[f->key]=f;
    }
    auto p=packet();p[33]=16;WINDIVERT_ADDRESS addr{};addr.IPChecksum=addr.TCPChecksum=1;
    std::vector<uint64_t> times;OwnerBatch owners;PassThroughBatch out;
    for(int n=0;n<30000;n++){out.addresses.clear();out.bytes.clear();auto start=qpcUs();processPacket(p,addr,owners,&out);times.push_back(qpcUs()-start);}
    std::sort(times.begin(),times.end());printf("R4 20000 closed tuples classify p99=%llu us, p50=%llu us\n",times[times.size()*99/100],times[times.size()/2]);
    prune();require(flows.empty(),"R4 closed tuples must not retain full Flow objects after pruning");
    std::unique_lock held(flowMutex);
    auto future=std::async(std::launch::async,[&](){OwnerBatch b;PassThroughBatch direct;return processPacket(p,addr,b,&direct)&&direct.addresses.size()==1;});
    bool unblocked=future.wait_for(std::chrono::milliseconds(200))==std::future_status::ready;held.unlock();
    require(future.get()&&unblocked,"R4 unrelated host traffic without live flows must not wait for flowMutex");
    for(unsigned i=0;i<40000;i++){
        auto f=std::make_shared<Flow>();f->key={htonl(i+1),inet_addr("198.51.100.89"),40000,443,6};f->close();flows[f->key]=f;
    }
    uint32_t pid=parentPid();RouteRule r{pid,910,created(pid),0,0};Core_SetProxy(910,"127.0.0.1",1080,"","");Core_ReplacePolicy(&r,1,L"\0");
    auto syn=packet();auto ownerTable=tcpOwner(pid,syn);auto before=sendCalls;
    require(processPacket(syn,addr,ownerTable)&&sendCalls==before+1,"R4 40000 closed tuples cannot consume the live-flow admission limit");
    flows.clear();aliases.clear();reset();
    Key closed{inet_addr("192.0.2.55"),inet_addr("198.51.100.55"),44000,443,6};
    quarantine(closed,44000,2000);quarantine(closed,44000,1000);
    require(retiredTuples[closed]==242000&&retiredAliases[44000]==242000,"R4 a late old close must not shorten a newer tuple or alias quarantine");reset();
}
static void r6Checks(uint32_t pid,uint64_t birth){
    reset();Core_SetProxy(910,"127.0.0.1",1080,"","");RouteRule sandbox{pid,910,birth,0,13};
    require(Core_ReplacePolicy(&sandbox,1,L"\0")==1,"R6 sandbox ancestry must support the no-Job flag");
    require(!processJobs.count({pid,birth}),"R6 sandbox identity must never be assigned to an xa sk5 Job");
    require(processLifetimes.count({pid,birth})==1,"R6 sandbox ancestry still pins root lifetime");
    wchar_t system[MAX_PATH];GetSystemDirectoryW(system,MAX_PATH);std::wstring executable=std::wstring(system)+L"\\ping.exe";
    std::wstring command=L"\""+executable+L"\" -n 60 127.0.0.1";STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};
    require(CreateProcessW(executable.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&si,&pi)!=0,"R6 child fixture launches");
    RouteRule found{};int action=route(pi.dwProcessId,created(pi.dwProcessId),false,found);
    TerminateProcess(pi.hProcess,0);WaitForSingleObject(pi.hProcess,5000);CloseHandle(pi.hThread);CloseHandle(pi.hProcess);
    require(action==1&&found.proxy==910,"R6 no-Job sandbox child must retain ancestry routing");reset();
}
int main(int argc,char** argv){
    try{
        if(argc<2)throw std::runtime_error("Pass the official WinDivert.dll path.");
        HMODULE dll=LoadLibraryA(argv[1]);require(dll!=nullptr,"official parser DLL must load");
        wdParsePacket=(decltype(wdParsePacket))GetProcAddress(dll,"WinDivertHelperParsePacket");officialChecksums=(decltype(wdChecksums))GetProcAddress(dll,"WinDivertHelperCalcChecksums");
        require(wdParsePacket&&officialChecksums,"official helpers must exist");
        wdChecksums=fakeChecksums;wdSend=fakeSend;wdSendEx=fakeSendEx;wdRecvEx=fakeRecvEx;divert=(HANDLE)1;
        WSADATA ws;WSAStartup(MAKEWORD(2,2),&ws);listenPort=18000;
        uint32_t pid=parentPid();uint64_t birth=created(pid);require(pid&&birth,"live parent identity required");
        if(argc>2){if(!strcmp(argv[2],"R6"))r6Checks(parentPid(),created(parentPid()));else if(!strcmp(argv[2],"R4"))r4Checks();else if(!strcmp(argv[2],"R5"))r5Checks();else if(!strcmp(argv[2],"R3"))r3Checks(pid,birth);else if(!strcmp(argv[2],"P0-5"))p05Checks();else if(!strcmp(argv[2],"P0-6"))p06Checks(pid,birth);else if(!strcmp(argv[2],"P0-4"))p04Checks();else if(!strcmp(argv[2],"P0-1"))p01Checks(pid,birth);else throw std::runtime_error("Unknown check");}
        else {r6Checks(parentPid(),created(parentPid()));r4Checks();r8Checks();r5Checks();r3Checks(pid,birth);p01Checks(pid,birth);p04Checks();p05Checks();p06Checks(pid,birth);batchLoopChecks();sendFailureChecks();policyChecks(pid,birth);concurrentActivityChecks();socketReuseChecks();followedTreeChecks(pid,birth);retryChecks();reportBenchmark();shutdownChecks();reset();telemetryChecks();}
        printf("PASS: %u packet-path assertions; no capture handle opened.\n",checks);WSACleanup();return 0;
    }catch(const std::exception& e){fprintf(stderr,"FAIL: %s\n",e.what());return 1;}
}
