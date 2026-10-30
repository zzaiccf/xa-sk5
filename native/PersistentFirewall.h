// Exact-image ALE safety policy. Production and explicit integration tests have
// different providers; the integration build can never select the production GUID.
#include <fwpmu.h>
#include <stdexcept>
#pragma comment(lib,"fwpuclnt.lib")
#pragma comment(lib,"rpcrt4.lib")
#pragma comment(lib,"uuid.lib")
namespace sandboxFirewall {
#ifdef XA_WFP_INTEGRATION
static GUID provider={0xeb758a61,0x7b8d,0x46d7,{0xa4,0xef,0x91,0x87,0x59,0x81,0x81,0x71}};
static GUID sublayer={0xeb758a62,0x7b8d,0x46d7,{0xa4,0xef,0x91,0x87,0x59,0x81,0x81,0x71}};
#else
static GUID provider={0x783a5e91,0x5835,0x43ad,{0x86,0x9e,0x6f,0xcd,0x5a,0x31,0x02,0x01}};
static GUID sublayer={0x783a5e92,0x5835,0x43ad,{0x86,0x9e,0x6f,0xcd,0x5a,0x31,0x02,0x01}};
#endif
static std::mutex mutex;
static HANDLE permitSession=nullptr;
static std::vector<std::wstring> images;
static std::vector<std::wstring> permitImages;
static std::vector<HANDLE> retiredPermitSessions;
static const GUID* layers[]={&FWPM_LAYER_ALE_AUTH_CONNECT_V4,&FWPM_LAYER_ALE_AUTH_CONNECT_V6,&FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4,&FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6};
struct Error{DWORD code;};
static void check(DWORD result){if(result!=ERROR_SUCCESS)throw Error{result};}
struct Session {
    HANDLE handle=nullptr;
    explicit Session(bool dynamic=false){FWPM_SESSION0 session{};session.flags=dynamic?FWPM_SESSION_FLAG_DYNAMIC:0;session.txnWaitTimeoutInMSec=5000;check(FwpmEngineOpen0(nullptr,RPC_C_AUTHN_WINNT,nullptr,&session,&handle));}
    ~Session(){if(handle)FwpmEngineClose0(handle);}
};
struct Transaction {
    HANDLE handle;bool committed=false;
    explicit Transaction(HANDLE h):handle(h){check(FwpmTransactionBegin0(handle,0));}
    void commit(){check(FwpmTransactionCommit0(handle));committed=true;}
    ~Transaction(){if(!committed)FwpmTransactionAbort0(handle);}
};
struct Blob {FWP_BYTE_BLOB* value=nullptr;explicit Blob(const std::wstring& path){check(FwpmGetAppIdFromFileName0(path.c_str(),&value));}~Blob(){if(value)FwpmFreeMemory0((void**)&value);}};
static void closeRetiredLocked(){
    DWORD error=0;
    for(auto it=retiredPermitSessions.begin();it!=retiredPermitSessions.end();){
        DWORD result=FwpmEngineClose0(*it);if(!result)it=retiredPermitSessions.erase(it);else{error=result;++it;}
    }
    check(error);
}
static void revokeLocked(){
    if(permitSession){retiredPermitSessions.push_back(permitSession);permitSession=nullptr;permitImages.clear();}
    closeRetiredLocked();
}
static DWORD withdraw() noexcept {
    try {std::lock_guard lk(mutex);revokeLocked();return 0;}catch(const Error& e){return e.code;}catch(...){return ERROR_UNHANDLED_EXCEPTION;}
}
static void ensureProvider(HANDLE handle){
    FWPM_PROVIDER0 p{};p.providerKey=provider;p.flags=FWPM_PROVIDER_FLAG_PERSISTENT;p.displayData.name=const_cast<wchar_t*>(L"xa sk5 sandbox protection");
    // No serviceName: persistent blocks must survive service disable/removal.
    DWORD error=FwpmProviderAdd0(handle,&p,nullptr);if(error!=FWP_E_ALREADY_EXISTS)check(error);
    FWPM_SUBLAYER0 s{};s.subLayerKey=sublayer;s.flags=FWPM_SUBLAYER_FLAG_PERSISTENT;s.providerKey=&provider;s.weight=0x7000;s.displayData.name=p.displayData.name;
    error=FwpmSubLayerAdd0(handle,&s,nullptr);if(error!=FWP_E_ALREADY_EXISTS)check(error);
}
struct OwnedFilter{UINT64 id;bool persistent;FWP_ACTION_TYPE action;};
static std::vector<OwnedFilter> owned(HANDLE handle){
    std::vector<OwnedFilter> result;
    for(auto layer:layers){
    FWPM_FILTER_ENUM_TEMPLATE0 scope{};scope.providerKey=&provider;scope.layerKey=*layer;
    scope.enumType=FWP_FILTER_ENUM_FULLY_CONTAINED;scope.actionMask=0xFFFFFFFF;
    scope.flags=FWP_FILTER_ENUM_FLAG_INCLUDE_DISABLED;
    HANDLE enumeration=nullptr;check(FwpmFilterCreateEnumHandle0(handle,&scope,&enumeration));
    try {for(;;){FWPM_FILTER0** page=nullptr;UINT32 count=0;check(FwpmFilterEnum0(handle,enumeration,256,&page,&count));
        try {for(UINT32 i=0;i<count;i++){auto f=page[i];if(f->providerKey&&IsEqualGUID(*f->providerKey,provider)&&IsEqualGUID(f->subLayerKey,sublayer))result.push_back({f->filterId,(f->flags&FWPM_FILTER_FLAG_PERSISTENT)!=0,f->action.type});}}
        catch(...){FwpmFreeMemory0((void**)&page);throw;}
        FwpmFreeMemory0((void**)&page);if(count<256)break;
    }}catch(...){FwpmFilterDestroyEnumHandle0(handle,enumeration);throw;}
    check(FwpmFilterDestroyEnumHandle0(handle,enumeration));
    }return result;
}
static void add(HANDLE handle,const std::wstring& path,bool permit){
    Blob app(path);FWPM_FILTER_CONDITION0 conditions[2]{};
    conditions[0].fieldKey=FWPM_CONDITION_ALE_APP_ID;conditions[0].matchType=FWP_MATCH_EQUAL;conditions[0].conditionValue.type=FWP_BYTE_BLOB_TYPE;conditions[0].conditionValue.byteBlob=app.value;
    conditions[1].fieldKey=FWPM_CONDITION_FLAGS;conditions[1].matchType=FWP_MATCH_FLAGS_NONE_SET;conditions[1].conditionValue.type=FWP_UINT32;conditions[1].conditionValue.uint32=FWP_CONDITION_FLAG_IS_LOOPBACK;
    UINT64 weight=permit?2:1;
    for(auto layer:layers){FWPM_FILTER0 filter{};filter.displayData.name=const_cast<wchar_t*>(permit?L"xa sk5 healthy capture permit":L"xa sk5 persistent sandbox block");filter.displayData.description=const_cast<wchar_t*>(path.c_str());
        filter.flags=permit?0:FWPM_FILTER_FLAG_PERSISTENT;filter.providerKey=&provider;filter.layerKey=*layer;filter.subLayerKey=sublayer;
        filter.weight.type=FWP_UINT64;filter.weight.uint64=&weight;filter.action.type=permit?FWP_ACTION_PERMIT:FWP_ACTION_BLOCK;
        // Soft permit does not override another provider's security policy.
        filter.numFilterConditions=2;filter.filterCondition=conditions;check(FwpmFilterAdd0(handle,&filter,nullptr,nullptr));
    }
}
static void allowLocked(){
    if(!active||captureFailed){revokeLocked();throw Error{ERROR_INVALID_STATE};}
    if(sandboxIdentityState!=1){revokeLocked();return;}
    closeRetiredLocked();
    if(permitSession&&images==permitImages)return;
    if(images.empty()){revokeLocked();return;}
    Session session(true);Transaction transaction(session.handle);
    for(const auto& path:images)add(session.handle,path,true);
    transaction.commit();
    if(!active||captureFailed||sandboxIdentityState!=1){revokeLocked();throw Error{ERROR_INVALID_STATE};}
    // Commit the complete replacement lease before retiring the old one. Keep
    // failed close handles so later allow/withdraw can retry without leaking them.
    if(permitSession)retiredPermitSessions.push_back(permitSession);
    permitSession=session.handle;session.handle=nullptr;permitImages=images;
    closeRetiredLocked();
}
static DWORD replace(const wchar_t* paths) noexcept {
    try {std::lock_guard lk(mutex);std::vector<std::wstring> next;
        if(paths)for(auto p=paths;*p;p+=wcslen(p)+1){if(next.size()>=4096)throw Error{ERROR_BUFFER_OVERFLOW};next.emplace_back(p);}
        std::sort(next.begin(),next.end());next.erase(std::unique(next.begin(),next.end()),next.end());
        Session session;Transaction transaction(session.handle);ensureProvider(session.handle);
        for(const auto& f:owned(session.handle))if(f.persistent)check(FwpmFilterDeleteById0(session.handle,f.id));
        for(const auto& path:next)add(session.handle,path,false);
        transaction.commit();images=std::move(next);
        if(active&&!captureFailed&&sandboxIdentityState==1)allowLocked();
        else revokeLocked();
        return 0;
    }catch(const Error& e){return e.code;}catch(...){return ERROR_UNHANDLED_EXCEPTION;}
}
static DWORD allow() noexcept {
    try {std::lock_guard lk(mutex);allowLocked();return 0;
    }catch(const Error& e){return e.code;}catch(...){return ERROR_UNHANDLED_EXCEPTION;}
}
static DWORD inspect(UINT32* blocks,UINT32* permits) noexcept {
    try {std::lock_guard lk(mutex);Session session;UINT32 b=0,p=0;for(const auto& f:owned(session.handle)){if(f.action==FWP_ACTION_BLOCK)b++;if(f.action==FWP_ACTION_PERMIT)p++;}if(blocks)*blocks=b;if(permits)*permits=p;return 0;}
    catch(const Error& e){return e.code;}catch(...){return ERROR_UNHANDLED_EXCEPTION;}
}
static DWORD clear() noexcept {
    try {std::lock_guard lk(mutex);revokeLocked();Session session;Transaction transaction(session.handle);
        for(const auto& f:owned(session.handle))check(FwpmFilterDeleteById0(session.handle,f.id));
        DWORD error=FwpmSubLayerDeleteByKey0(session.handle,&sublayer);if(error!=FWP_E_SUBLAYER_NOT_FOUND)check(error);
        error=FwpmProviderDeleteByKey0(session.handle,&provider);if(error!=FWP_E_PROVIDER_NOT_FOUND)check(error);
        transaction.commit();images.clear();return 0;
    }catch(const Error& e){return e.code;}catch(...){return ERROR_UNHANDLED_EXCEPTION;}
}
}
API DWORD Core_FirewallReplace(const wchar_t* paths){return sandboxFirewall::replace(paths);}
API DWORD Core_FirewallWithdraw(){return sandboxFirewall::withdraw();}
API DWORD Core_FirewallAllow(){return sandboxFirewall::allow();}
API DWORD Core_FirewallInspect(UINT32* blocks,UINT32* permits){return sandboxFirewall::inspect(blocks,permits);}
API DWORD Core_FirewallClear(){return sandboxFirewall::clear();}
