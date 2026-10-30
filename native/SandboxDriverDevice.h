#pragma once
#include <windows.h>
#include <cstdint>
// Read-only API_GET_VERSION/API_QUERY_PROCESS adapter for Sandboxie Plus 1.18.8.
// Check the opened driver, not an on-disk DLL version.
#include <winternl.h>
struct SandboxDevice {
    using Open=NTSTATUS(NTAPI*)(PHANDLE,ACCESS_MASK,POBJECT_ATTRIBUTES,PIO_STATUS_BLOCK,ULONG,ULONG);
    using Ioctl=NTSTATUS(NTAPI*)(HANDLE,HANDLE,PIO_APC_ROUTINE,PVOID,PIO_STATUS_BLOCK,ULONG,PVOID,ULONG,PVOID,ULONG);
    HANDLE handle=nullptr;
    Ioctl ioctl=nullptr;
    SandboxDevice(){
        auto module=GetModuleHandleW(L"ntdll.dll");
        auto open=(Open)GetProcAddress(module,"NtOpenFile");
        ioctl=(Ioctl)GetProcAddress(module,"NtDeviceIoControlFile");
        if(!open||!ioctl)return;
        wchar_t path[]=L"\\Device\\SandboxieDriverApi";
        UNICODE_STRING name{sizeof(path)-sizeof(wchar_t),sizeof(path),path};
        OBJECT_ATTRIBUTES attr{};attr.Length=sizeof(attr);attr.ObjectName=&name;attr.Attributes=0x40;
        IO_STATUS_BLOCK io{};
        if(open(&handle,FILE_GENERIC_READ,&attr,&io,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,0x20)<0)handle=nullptr;
    }
    ~SandboxDevice(){if(handle)CloseHandle(handle);}
    NTSTATUS call(uint64_t (&args)[8]){
        if(!handle)return (NTSTATUS)0xc000000e;
        IO_STATUS_BLOCK io{};
        return ioctl(handle,nullptr,nullptr,nullptr,&io,0x222007,args,sizeof(args),nullptr,0);
    }
    wchar_t version[64]{};ULONG abi=0;
    bool compatible(){
        alignas(8) uint64_t args[8]{0x12340001,(uint64_t)version,(uint64_t)&abi};
        return call(args)>=0&&wcscmp(version,L"5.73.8")==0&&abi==0x57230;
    }
};
