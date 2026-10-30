// Explicit administrator-only BFE test. Never touches the production provider,
// WinDivert, Sandboxie, services, proxies, or game images.
#include <winsock2.h>
#include <windows.h>
#include <ws2tcpip.h>
#include <atomic>
#include <mutex>
#include <vector>
#include <string>
#include <algorithm>
#include <cstdio>
#include <stdexcept>
#define API extern "C"
#define XA_WFP_INTEGRATION
static std::atomic<bool> active{true},captureFailed{false};
static std::atomic<int> sandboxIdentityState{1}; // This fixture validates WFP, not Sandboxie.
#include "../../native/PersistentFirewall.h"
static void require(bool ok,const char* text){if(!ok)throw std::runtime_error(text);}
static bool connectTo(const wchar_t* ip,unsigned port){
    SOCKET s=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);sockaddr_in addr{};addr.sin_family=AF_INET;addr.sin_port=htons((u_short)port);
    if(InetPtonW(AF_INET,ip,&addr.sin_addr)!=1){closesocket(s);return false;}
    u_long nonblocking=1;ioctlsocket(s,FIONBIO,&nonblocking);int result=connect(s,(sockaddr*)&addr,sizeof(addr));
    if(result==0){closesocket(s);return true;}
    if(WSAGetLastError()!=WSAEWOULDBLOCK){closesocket(s);return false;}
    fd_set ready;FD_ZERO(&ready);FD_SET(s,&ready);timeval wait{2,0};int error=1,size=sizeof(error);
    bool ok=select(0,nullptr,&ready,nullptr,&wait)>0&&getsockopt(s,SOL_SOCKET,SO_ERROR,(char*)&error,&size)==0&&error==0;closesocket(s);return ok;
}
static PROCESS_INFORMATION launch(const std::wstring& exe,const std::wstring& args){
    STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};auto command=L"\""+exe+L"\" "+args;
    require(CreateProcessW(exe.c_str(),command.data(),nullptr,nullptr,FALSE,CREATE_NO_WINDOW,nullptr,nullptr,&si,&pi)!=0,"synthetic helper launch failed");CloseHandle(pi.hThread);return pi;
}
static bool probe(const std::wstring& exe,const std::wstring& ip,const std::wstring& port){
    auto child=launch(exe,L"--probe "+ip+L" "+port);DWORD code=1;
    if(WaitForSingleObject(child.hProcess,5000)==WAIT_OBJECT_0)GetExitCodeProcess(child.hProcess,&code);else{TerminateProcess(child.hProcess,2);WaitForSingleObject(child.hProcess,5000);}
    CloseHandle(child.hProcess);return code==0;
}
int wmain(int argc,wchar_t** argv){
    WSADATA data{};WSAStartup(MAKEWORD(2,2),&data);wchar_t file[32768];GetModuleFileNameW(nullptr,file,32768);std::wstring exe=file;
    if(argc==4&&!wcscmp(argv[1],L"--probe"))return connectTo(argv[2],wcstoul(argv[3],nullptr,10))?0:1;
    if(argc==4&&!wcscmp(argv[1],L"--run-approved")&&!wcscmp(argv[2],L"--lease")){
        HANDLE ready=OpenEventW(EVENT_MODIFY_STATE,FALSE,argv[3]);if(!ready)return 3;
        sandboxFirewall::images={exe};if(Core_FirewallAllow()!=0){CloseHandle(ready);return 4;}SetEvent(ready);CloseHandle(ready);Sleep(INFINITE);return 0;
    }
    bool cleanupOnly=argc==2&&!wcscmp(argv[1],L"--cleanup-test-approved");
    if(!cleanupOnly&&(argc!=4||wcscmp(argv[1],L"--run-approved"))){puts("Build-only by default. Requires --run-approved <non-loopback IPv4 test endpoint> <TCP port> as administrator. Recovery: --cleanup-test-approved (test GUID only).");return 2;}
    SID_IDENTIFIER_AUTHORITY authority=SECURITY_NT_AUTHORITY;PSID admins=nullptr;BOOL administrator=FALSE;
    AllocateAndInitializeSid(&authority,2,SECURITY_BUILTIN_DOMAIN_RID,DOMAIN_ALIAS_RID_ADMINS,0,0,0,0,0,0,&admins);CheckTokenMembership(nullptr,admins,&administrator);FreeSid(admins);
    if(!administrator){puts("Administrator token required.");return 2;}
    if(cleanupOnly){DWORD result=Core_FirewallClear();printf("Test-GUID cleanup result=%lu\n",result);return result?1:0;}
    IN_ADDR ip{};unsigned port=wcstoul(argv[3],nullptr,10);
    if(InetPtonW(AF_INET,argv[2],&ip)!=1||(ntohl(ip.s_addr)>>24)==127||!port||port>65535){puts("Use a reachable, controlled non-loopback IPv4 endpoint and port.");return 2;}
    HANDLE mutex=CreateMutexW(nullptr,FALSE,L"Local\\xa-sk5-WFP-integration-eb758a61");DWORD acquired=WaitForSingleObject(mutex,0);
    if(acquired!=WAIT_OBJECT_0&&acquired!=WAIT_ABANDONED){CloseHandle(mutex);return 2;}
    int result=1;HANDLE lease=nullptr;SOCKET local=INVALID_SOCKET;HANDLE ready=nullptr;
    try {
        sandboxFirewall::check(Core_FirewallClear());require(probe(exe,argv[2],argv[3]),"baseline TCP connection failed; cannot infer WFP results");
        active=false;auto paths=exe+L'\0'+L'\0';sandboxFirewall::check(Core_FirewallReplace(paths.c_str()));UINT32 b=0,p=0;
        sandboxFirewall::check(Core_FirewallInspect(&b,&p));require(b==4&&p==0,"four persistent ALE filters required");
        require(!probe(exe,argv[2],argv[3]),"persistent block did not deny new outbound TCP");
        local=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);sockaddr_in a{};a.sin_family=AF_INET;a.sin_addr.s_addr=htonl(INADDR_LOOPBACK);int size=sizeof(a);
        require(bind(local,(sockaddr*)&a,sizeof(a))==0&&listen(local,4)==0&&getsockname(local,(sockaddr*)&a,&size)==0,"loopback listener failed");
        require(probe(exe,L"127.0.0.1",std::to_wstring(ntohs(a.sin_port))),"loopback must be excluded");
        active=true;sandboxFirewall::check(Core_FirewallAllow());sandboxFirewall::check(Core_FirewallInspect(&b,&p));require(b==4&&p==4,"dynamic permit filters missing");
        require(probe(exe,argv[2],argv[3]),"higher-weight dynamic permit did not restore TCP");
        sandboxFirewall::check(Core_FirewallWithdraw());require(!probe(exe,argv[2],argv[3]),"explicit withdrawal did not restore block");
        std::wstring event=L"Local\\xa-sk5-WFP-lease-"+std::to_wstring(GetCurrentProcessId());ready=CreateEventW(nullptr,TRUE,FALSE,event.c_str());
        auto child=launch(exe,L"--run-approved --lease "+event);lease=child.hProcess;require(WaitForSingleObject(ready,10000)==WAIT_OBJECT_0,"lease child failed");
        require(probe(exe,argv[2],argv[3]),"child-owned dynamic session did not allow TCP");
        TerminateProcess(lease,0);WaitForSingleObject(lease,5000);CloseHandle(lease);lease=nullptr;
        bool blocked=false;for(int n=0;n<20;n++){if(!probe(exe,argv[2],argv[3])){blocked=true;break;}Sleep(250);}
        require(blocked,"process death did not withdraw dynamic permit");sandboxFirewall::check(Core_FirewallInspect(&b,&p));require(b==4&&p==0,"persistent filters must survive lease-owner death");
        puts("PASS: real BFE persistence, dynamic arbitration, withdrawal, process-death rundown and loopback exclusion. IPv6/UDP/inbound and reboot remain separate live acceptance steps.");result=0;
    }catch(const sandboxFirewall::Error& e){printf("WFP error 0x%08lx\n",e.code);}catch(const std::exception& e){printf("FAIL: %s\n",e.what());}
    if(lease){TerminateProcess(lease,2);WaitForSingleObject(lease,5000);CloseHandle(lease);}if(ready)CloseHandle(ready);if(local!=INVALID_SOCKET)closesocket(local);
    DWORD cleanup=Core_FirewallClear();if(cleanup){printf("TEST PROVIDER CLEANUP FAILED 0x%08lx; rerun the approved fixture to clean eb758a61/eb758a62 only.\n",cleanup);result=1;}
    ReleaseMutex(mutex);CloseHandle(mutex);WSACleanup();return result;
}
