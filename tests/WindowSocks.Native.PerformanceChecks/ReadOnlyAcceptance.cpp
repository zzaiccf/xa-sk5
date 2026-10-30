// Explicit read-only live probe. No service, filter or Sandboxie mutations.
#include <windows.h>
#include <fwpmu.h>
#include <tlhelp32.h>
#include <cstdio>
#include "../../native/SandboxDriverDevice.h"
#pragma comment(lib,"fwpuclnt.lib")
#pragma comment(lib,"advapi32.lib")
int wmain(int argc,wchar_t** argv){
    if(argc!=2||wcscmp(argv[1],L"--read-approved")){puts("Prepared only; --read-approved requires an existing SYSTEM context.");return 2;}
    HANDLE token=nullptr;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))return 3;
    BYTE buffer[1024];DWORD size=0;bool system=false;
    if(GetTokenInformation(token,TokenUser,buffer,sizeof(buffer),&size))system=IsWellKnownSid(((TOKEN_USER*)buffer)->User.Sid,WinLocalSystemSid)!=FALSE;
    CloseHandle(token);if(!system){puts("SYSTEM context required; nothing queried.");return 3;}
    SandboxDevice driver;bool compatible=driver.compatible();
    wprintf(L"SYSTEM=true\nSandboxieVersion=%s ABI=0x%lX Compatible=%s\n",driver.version[0]?driver.version:L"unavailable",driver.abi,compatible?L"true":L"false");
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);PROCESSENTRY32W entry{};entry.dwSize=sizeof(entry);
    if(snapshot!=INVALID_HANDLE_VALUE&&Process32FirstW(snapshot,&entry))do{
        if(entry.th32ProcessID!=4&&_wcsicmp(entry.szExeFile,L"lsass.exe")&&_wcsicmp(entry.szExeFile,L"MsMpEng.exe")&&_wcsicmp(entry.szExeFile,L"services.exe"))continue;
        HANDLE p=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,entry.th32ProcessID);DWORD error=p?0:GetLastError();FILETIME birth{},exit{},kernel{},user{};
        if(p&&!GetProcessTimes(p,&birth,&exit,&kernel,&user))error=GetLastError();if(p)CloseHandle(p);
        ULARGE_INTEGER value{};value.LowPart=birth.dwLowDateTime;value.HighPart=birth.dwHighDateTime;
        wprintf(L"Process=%s PID=%lu Birth=%llu Win32Error=%lu (protection level not inferred)\n",entry.szExeFile,entry.th32ProcessID,value.QuadPart,error);
    }while(Process32NextW(snapshot,&entry));if(snapshot!=INVALID_HANDLE_VALUE)CloseHandle(snapshot);
    GUID provider={0x783a5e91,0x5835,0x43ad,{0x86,0x9e,0x6f,0xcd,0x5a,0x31,0x02,0x01}},sublayer={0x783a5e92,0x5835,0x43ad,{0x86,0x9e,0x6f,0xcd,0x5a,0x31,0x02,0x01}};
    HANDLE engine=nullptr;DWORD error=FwpmEngineOpen0(nullptr,RPC_C_AUTHN_WINNT,nullptr,nullptr,&engine);
    printf("WfpReadOpen=%lu\n",error);if(error)return 4;
    FWPM_PROVIDER0* p=nullptr;FWPM_SUBLAYER0* s=nullptr;
    printf("ProductProviderRead=%lu (0=present; 0x80320005=absent)\n",FwpmProviderGetByKey0(engine,&provider,&p));
    printf("ProductSublayerRead=%lu (0=present; 0x80320007=absent)\n",FwpmSubLayerGetByKey0(engine,&sublayer,&s));
    if(p)FwpmFreeMemory0((void**)&p);if(s)FwpmFreeMemory0((void**)&s);FwpmEngineClose0(engine);return compatible?0:5;
}
