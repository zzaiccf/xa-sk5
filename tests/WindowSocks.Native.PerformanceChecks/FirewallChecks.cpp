// Runs the production WFP policy against an in-memory implementation of the
// Windows API boundary. No BFE handles, filters or network operations are used.
#include <windows.h>
#include <fwpmu.h>
#include <algorithm>
#include <atomic>
#include <map>
#include <mutex>
#include <string>
#include <vector>
#include <stdexcept>
#include <cstdio>
static std::atomic<bool> active{false},captureFailed{false};
static std::atomic<int> sandboxIdentityState{1};
static unsigned assertions=0;
static void require(bool ok,const char* why){++assertions;if(!ok)throw std::runtime_error(why);}
struct FakeFilter {UINT64 id;HANDLE session;GUID provider,sublayer,layer;bool persistent;FWP_ACTION_TYPE action;std::wstring path;};
static std::map<UINT64,FakeFilter> filters,staged;
static std::map<HANDLE,bool> sessions;
static UINT64 serial=0;static HANDLE transaction=nullptr;
static bool track=false,gap=false,failPermit=false,failBlock=false,failClose=false,scoped=false;
static bool permitted(const std::wstring& path){return std::count_if(filters.begin(),filters.end(),[&](const auto& x){return x.second.path==path&&x.second.action==FWP_ACTION_PERMIT;})>=4;}
static void observe(){if(track&&!permitted(L"A.exe"))gap=true;}
static DWORD fakeOpen(PCWSTR,UINT32,SEC_WINNT_AUTH_IDENTITY_W*,const FWPM_SESSION0* s,HANDLE* h){*h=(HANDLE)++serial;sessions[*h]=(s->flags&FWPM_SESSION_FLAG_DYNAMIC)!=0;observe();return 0;}
static DWORD fakeClose(HANDLE h){if(failClose&&sessions[h])return ERROR_BUSY;for(auto it=filters.begin();it!=filters.end();)if(it->second.session==h&&!it->second.persistent)it=filters.erase(it);else ++it;sessions.erase(h);observe();return 0;}
static DWORD fakeBegin(HANDLE h,UINT32){transaction=h;staged=filters;return 0;}
static DWORD fakeCommit(HANDLE){filters=staged;transaction=nullptr;observe();return 0;}
static DWORD fakeAbort(HANDLE){staged.clear();transaction=nullptr;return 0;}
static DWORD fakeProvider(HANDLE,const FWPM_PROVIDER0*,PSECURITY_DESCRIPTOR){return 0;}
static DWORD fakeSublayer(HANDLE,const FWPM_SUBLAYER0*,PSECURITY_DESCRIPTOR){return 0;}
static DWORD fakeDeleteKey(HANDLE,const GUID*){return 0;}
static GUID enumProvider{},enumLayer{};
static DWORD fakeEnumCreate(HANDLE,const FWPM_FILTER_ENUM_TEMPLATE0* t,HANDLE* h){scoped=t&&t->providerKey;if(scoped){enumProvider=*t->providerKey;enumLayer=t->layerKey;require(t->actionMask==0xFFFFFFFF,"N4 enumerate both BLOCK and PERMIT");}*h=(HANDLE)999;return 0;}
static DWORD fakeEnum(HANDLE,HANDLE,UINT32,FWPM_FILTER0*** page,UINT32* count){
    auto source=transaction?staged:filters;
    if(scoped)for(auto it=source.begin();it!=source.end();)if(!IsEqualGUID(it->second.provider,enumProvider)||!IsEqualGUID(it->second.layer,enumLayer))it=source.erase(it);else ++it;
    *count=(UINT32)source.size();
    auto bytes=(sizeof(FWPM_FILTER0*)+sizeof(FWPM_FILTER0)+sizeof(GUID))*source.size();
    *page=(FWPM_FILTER0**)HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,bytes?bytes:1);
    auto rows=(FWPM_FILTER0*)(*page+source.size());auto providers=(GUID*)(rows+source.size());size_t i=0;
    for(const auto& [id,f]:source){auto& row=rows[i];providers[i]=f.provider;row.filterId=id;row.providerKey=&providers[i];row.subLayerKey=f.sublayer;row.flags=f.persistent?FWPM_FILTER_FLAG_PERSISTENT:0;row.action.type=f.action;(*page)[i]=&row;++i;}return 0;
}
static DWORD fakeEnumDestroy(HANDLE,HANDLE){return 0;}
static void fakeFree(void** p){HeapFree(GetProcessHeap(),0,*p);*p=nullptr;}
static DWORD fakeApp(PCWSTR path,FWP_BYTE_BLOB** blob){size_t bytes=(wcslen(path)+1)*sizeof(wchar_t);*blob=(FWP_BYTE_BLOB*)HeapAlloc(GetProcessHeap(),HEAP_ZERO_MEMORY,sizeof(FWP_BYTE_BLOB)+bytes);(*blob)->size=(UINT32)bytes;(*blob)->data=(UINT8*)(*blob+1);memcpy((*blob)->data,path,bytes);return 0;}
static DWORD fakeAdd(HANDLE h,const FWPM_FILTER0* f,PSECURITY_DESCRIPTOR,UINT64*){
    if((failPermit&&f->action.type==FWP_ACTION_PERMIT)||(failBlock&&f->action.type==FWP_ACTION_BLOCK))return ERROR_ACCESS_DENIED;
    UINT64 id=++serial;auto& target=transaction?staged:filters;target[id]={id,h,*f->providerKey,f->subLayerKey,f->layerKey,(f->flags&FWPM_FILTER_FLAG_PERSISTENT)!=0,f->action.type,(wchar_t*)f->filterCondition[0].conditionValue.byteBlob->data};observe();return 0;
}
static DWORD fakeDelete(HANDLE,UINT64 id){(transaction?staged:filters).erase(id);observe();return 0;}
#define FwpmEngineOpen0 fakeOpen
#define FwpmEngineClose0 fakeClose
#define FwpmTransactionBegin0 fakeBegin
#define FwpmTransactionCommit0 fakeCommit
#define FwpmTransactionAbort0 fakeAbort
#define FwpmProviderAdd0 fakeProvider
#define FwpmSubLayerAdd0 fakeSublayer
#define FwpmProviderDeleteByKey0 fakeDeleteKey
#define FwpmSubLayerDeleteByKey0 fakeDeleteKey
#define FwpmFilterCreateEnumHandle0 fakeEnumCreate
#define FwpmFilterEnum0 fakeEnum
#define FwpmFilterDestroyEnumHandle0 fakeEnumDestroy
#define FwpmFreeMemory0 fakeFree
#define FwpmGetAppIdFromFileName0 fakeApp
#define FwpmFilterAdd0 fakeAdd
#define FwpmFilterDeleteById0 fakeDelete
#define API
#include "../../native/PersistentFirewall.h"
int main(int argc,char** argv){try{
    require(sandboxFirewall::replace(L"A.exe\0")==0,"initial persistent block");active=true;
    require(sandboxFirewall::allow()==0&&permitted(L"A.exe"),"initial A permit");track=true;
    if(argc>1&&std::string(argv[1])=="N4"){
        auto foreign=filters.begin()->second;foreign.id=++serial;foreign.provider.Data1^=1;filters[foreign.id]=foreign;
        auto otherLayer=filters.begin()->second;otherLayer.id=++serial;otherLayer.sublayer.Data1^=1;filters[otherLayer.id]=otherLayer;
        UINT32 b,p;require(sandboxFirewall::inspect(&b,&p)==0,"inspect");require(scoped&&IsEqualGUID(enumProvider,sandboxFirewall::provider),"N4 enumeration must specify the product provider");
        require(b==4&&p==4,"N4 inspect excludes foreign provider and foreign sublayer");
        track=false;require(sandboxFirewall::clear()==0&&filters.size()==2,"N4 cleanup preserves foreign provider/sublayer filters");filters.clear();
    }else{
        require(sandboxFirewall::replace(L"A.exe\0B.exe\0")==0,"replace AB");
        require(!gap&&permitted(L"A.exe")&&permitted(L"B.exe"),"N2 replace must retain A permit at every API boundary and immediately permit B");
        failPermit=true;require(sandboxFirewall::replace(L"A.exe\0B.exe\0C.exe\0")!=0,"permit failure surfaced");failPermit=false;
        require(!gap&&permitted(L"A.exe")&&permitted(L"B.exe")&&!permitted(L"C.exe"),"N2 failed new lease preserves old lease");
        require(sandboxFirewall::allow()==0&&permitted(L"C.exe"),"N2 allow retries changed images despite an existing lease");
        failBlock=true;require(sandboxFirewall::replace(L"A.exe\0")!=0,"block failure surfaced");failBlock=false;
        require(!gap&&permitted(L"C.exe"),"N2 failed block transaction retains old lease");
        require(sandboxFirewall::replace(L"A.exe\0")==0&&!gap&&!permitted(L"B.exe"),"N2 remove B keeps A permitted");
        failClose=true;require(sandboxFirewall::replace(L"A.exe\0B.exe\0")!=0,"old-session close failure surfaced");failClose=false;
        require(!gap&&permitted(L"A.exe"),"N2 close failure retains A");
        require(sandboxFirewall::allow()==0&&permitted(L"B.exe"),"N2 close failure is retryable");
        track=false;captureFailed=true;require(sandboxFirewall::allow()!=0&&!permitted(L"A.exe"),"unhealthy capture withdraws all permits");
    }
    track=false;require(sandboxFirewall::clear()==0&&filters.empty(),"fixture cleanup");
    printf("PASS: %u firewall assertions; fake BFE only.\n",assertions);return 0;
}catch(const std::exception& ex){fprintf(stderr,"FAIL: %s\n",ex.what());return 1;}}
