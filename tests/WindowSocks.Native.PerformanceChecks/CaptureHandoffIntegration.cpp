// Approved stage-C harness: production capture replacement, real WinDivert,
// scoped to one dedicated TCP endpoint. Never installs WFP policy or changes
// the installed service. The service must already be stopped by the operator.
#include "../../native/WindowSocksNative.cpp"
static decltype(wdOpen) realOpen;
static decltype(wdClose) realClose;
static decltype(wdShutdown) realShutdown;
static std::string filter;
static std::vector<std::string> steps;
static bool failNextOpen=false;
static HANDLE scopedOpen(const char*,WINDIVERT_LAYER layer,INT16 priority,UINT64 flags){
    steps.push_back("open:"+std::to_string(priority));
    if(failNextOpen){SetLastError(ERROR_ACCESS_DENIED);return INVALID_HANDLE_VALUE;}
    return realOpen(filter.c_str(),layer,priority,flags);
}
static BOOL trackedShutdown(HANDLE h,WINDIVERT_SHUTDOWN how){steps.push_back("shutdown");return realShutdown(h,how);}
static BOOL trackedClose(HANDLE h){steps.push_back("close");return realClose(h);}
static void require(bool ok,const char* why){if(!ok)throw std::runtime_error(why);}
static bool connectProbe(const char* endpoint,unsigned port){
    SOCKET s=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);sockaddr_in a{};a.sin_family=AF_INET;a.sin_port=htons((u_short)port);InetPtonA(AF_INET,endpoint,&a.sin_addr);
    u_long nonblocking=1;ioctlsocket(s,FIONBIO,&nonblocking);int result=connect(s,(sockaddr*)&a,sizeof(a));
    fd_set write;FD_ZERO(&write);FD_SET(s,&write);timeval wait{3,0};int error=1,len=sizeof(error);
    bool ok=result==0||(select(0,nullptr,&write,nullptr,&wait)>0&&getsockopt(s,SOL_SOCKET,SO_ERROR,(char*)&error,&len)==0&&error==0);closesocket(s);return ok;
}
int main(int argc,char** argv){
    if(argc!=5||strcmp(argv[1],"--run-approved")){puts("Prepared only. --run-approved <official WinDivert.dll> <dedicated IPv4> <TCP port>; games closed and installed WindowSocks service stopped first.");return 2;}
    IN_ADDR address{};unsigned port=strtoul(argv[4],nullptr,10);
    if(InetPtonA(AF_INET,argv[3],&address)!=1||!port||port>65535||(ntohl(address.s_addr)>>24)==127)return 2;
    SC_HANDLE manager=OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT);if(!manager)return 3;
    SC_HANDLE service=OpenServiceW(manager,L"WindowSocks",SERVICE_QUERY_STATUS);SERVICE_STATUS status{};
    bool stopped=service?QueryServiceStatus(service,&status)&&status.dwCurrentState==SERVICE_STOPPED:GetLastError()==ERROR_SERVICE_DOES_NOT_EXIST;
    if(service)CloseServiceHandle(service);CloseServiceHandle(manager);if(!stopped){puts("Installed service is not confirmed stopped; refusing overlapping capture.");return 3;}
    WSADATA data{};WSAStartup(MAKEWORD(2,2),&data);int result=1;
    try{
        filter="outbound and ip and tcp and ip.DstAddr == "+std::string(argv[3])+" and tcp.DstPort == "+std::to_string(port);
        require(connectProbe(argv[3],port),"baseline endpoint unavailable");
        driverLibrary=LoadLibraryA(argv[2]);require(driverLibrary!=nullptr,"official WinDivert DLL load failed");
#define READ(name) wd##name=(decltype(wd##name))GetProcAddress(driverLibrary,"WinDivert" #name);require(wd##name!=nullptr,"missing WinDivert " #name)
        READ(Open);READ(Close);READ(Shutdown);READ(RecvEx);READ(Send);READ(SendEx);
#undef READ
        wdParsePacket=(decltype(wdParsePacket))GetProcAddress(driverLibrary,"WinDivertHelperParsePacket");wdChecksums=(decltype(wdChecksums))GetProcAddress(driverLibrary,"WinDivertHelperCalcChecksums");
        require(wdParsePacket&&wdChecksums,"parser exports missing");
        realOpen=wdOpen;realClose=wdClose;realShutdown=wdShutdown;wdOpen=scopedOpen;wdClose=trackedClose;wdShutdown=trackedShutdown;
        divert=wdOpen(nullptr,WINDIVERT_LAYER_NETWORK,100,0);require(divert!=INVALID_HANDLE_VALUE,"initial capture failed (admin/driver required)");
        active=true;captureFailed=false;capturePriority=100;captureThread=std::thread(captureLoop);
        require(connectProbe(argv[3],port),"baseline captured connection failed");
        require(packetMetrics.packets.load()>0,"no real captured packets observed");
        failNextOpen=true;captureFailed=true;steps.clear();auto old=divert;
        require(Core_ReplaceCapture()==0&&divert==old&&steps==std::vector<std::string>{"open:101"},"failed open did not preserve old capture");
        failNextOpen=false;
        for(int pass=0;pass<2;pass++){
            captureFailed=true;steps.clear();std::atomic<unsigned> sent{0},successful{0};
            std::thread traffic([&](){for(int i=0;i<10;i++){++sent;if(connectProbe(argv[3],port))++successful;Sleep(100);}});
            int replaced=Core_ReplaceCapture();traffic.join();
            for(const auto& step:steps)printf("%s\n",step.c_str());
            printf("pass=%d connections=%u successful=%u captured_packets=%llu\n",pass,sent.load(),successful.load(),packetMetrics.packets.load());
            require(replaced&&steps==std::vector<std::string>{pass==0?"open:101":"open:100","shutdown","close"},"make-before-break order failed");
            require(successful==sent,"connection loss observed during scoped handoff");
        }
        puts("PASS: scoped real capture handoff only; not proof of installed-service routing, UDP/IPv6 or absence of leaked FIN/RST.");result=0;
    }catch(const std::exception& ex){fprintf(stderr,"FAIL: %s (Win32=%lu)\n",ex.what(),GetLastError());}
    active=false;
    if(divert!=INVALID_HANDLE_VALUE){if(realShutdown)realShutdown(divert,WINDIVERT_SHUTDOWN_RECV);if(captureThread.joinable())captureThread.join();if(realClose)realClose(divert);}
    if(driverLibrary)FreeLibrary(driverLibrary);WSACleanup();return result;
}
