#include "SandboxDriverDevice.h"
static std::mutex sandboxHealthMutex;
static unsigned sandboxDriverFailures=0;
static uint64_t sandboxDriverFailureSince=0;
static std::wstring sandboxDriverDetail=L"尚未查詢";
static std::atomic<bool> sandboxDriverCheckRequested{false};
static void recordSandboxDriverHealth(int status,uint64_t now){
    if(status==1){sandboxDriverFailures=0;sandboxDriverFailureSince=0;sandboxIdentityState=1;}
    else {
        if(!sandboxDriverFailures)sandboxDriverFailureSince=now;
        ++sandboxDriverFailures;
        // Never enable an unverified driver. Established identities tolerate a
        // transient outage; only the control thread confirms a global failure.
        if(sandboxIdentityState==0||sandboxDriverFailures>=3||now-sandboxDriverFailureSince>=15000)sandboxIdentityState=-1;
    }
}
static int checkSandboxDriver(){
    std::lock_guard lk(sandboxHealthMutex);SandboxDevice device;int status=device.compatible()?1:-1;
    wchar_t detail[160]{};
    if(device.version[0])swprintf_s(detail,L"Sandboxie 版本 %s / ABI 0x%lX%s",device.version,device.abi,status==1?L"":L" 未經驗證，沙盒功能已停用");
    else wcscpy_s(detail,L"Sandboxie 驅動暫時無法讀取，正在自動重試");
    sandboxDriverDetail=detail;recordSandboxDriverHealth(status,GetTickCount64());sandboxDriverCheckRequested=false;return status;
}
static int querySandboxIdentity(uint32_t pid,uint64_t birth,std::wstring& box){
    if(!birth||created(pid)!=birth)return 0;
    SandboxDevice device;if(!device.compatible())return -2; // Driver-level; distinct from an unknown PID.
    wchar_t buffer[34]{};UNICODE_STRING boxString{0,sizeof(buffer),buffer};uint64_t driverBirth=0;
    alignas(8) uint64_t args[8]{0x12340007,pid,(uint64_t)&boxString,0,0,0,(uint64_t)&driverBirth,0};
    NTSTATUS status=device.call(args);
    if(created(pid)!=birth)return 0;
    if((uint32_t)status==0xc000000b)return 0;
    if(status<0||driverBirth!=birth||!buffer[0]||boxString.Length>=sizeof(buffer))return -1;
    box.assign(buffer,boxString.Length/sizeof(wchar_t));return 1;
}
struct SandboxIdentityEntry{int status;std::wstring box;uint64_t checked;};
static std::mutex sandboxIdentityMutex;
static std::map<std::pair<uint32_t,uint64_t>,SandboxIdentityEntry> sandboxIdentityCache;
static std::deque<std::pair<uint32_t,uint64_t>> sandboxIdentityOrder;
static std::atomic<std::shared_ptr<const std::vector<std::wstring>>> sandboxBoxes{std::make_shared<const std::vector<std::wstring>>()};
static void rebuildSandboxBoxes(){ // Exclusive stateMutex is held.
    auto next=std::make_shared<std::vector<std::wstring>>();
    for(const auto& guard:guards)if(guard.starts_with(L"@box:"))next->push_back(guard.substr(5));
    sandboxBoxes.store(next);
}
static bool pendingSandbox(uint32_t pid,uint64_t birth){
    const auto boxes=sandboxBoxes.load();
    if(boxes->empty()||!birth||!pid||pid==UINT32_MAX)return false;
    std::lock_guard lk(sandboxIdentityMutex);auto key=std::make_pair(pid,birth);auto it=sandboxIdentityCache.find(key);auto now=GetTickCount64();
    if(it==sandboxIdentityCache.end()||(it->second.status<0&&now-it->second.checked>=500)){
        std::wstring box;
        int status=sandboxIdentityQuery?sandboxIdentityQuery(pid,birth,box):sandboxIdentityState.load()==1?querySandboxIdentity(pid,birth,box):-1;
        if(it==sandboxIdentityCache.end()){
            while(sandboxIdentityCache.size()>=4096&&!sandboxIdentityOrder.empty()){
                sandboxIdentityCache.erase(sandboxIdentityOrder.front());sandboxIdentityOrder.pop_front();
            }
            sandboxIdentityOrder.push_back(key);
        }
        it=sandboxIdentityCache.insert_or_assign(key,SandboxIdentityEntry{status,box,now}).first;
    }
    if(it->second.status<0){if(it->second.status==-2)sandboxDriverCheckRequested=true;return false;}
    // Unknown identity must not quarantine the host. Verified sandbox/ancestor
    // rules and protected directories remain independently blocked.
    return it->second.status==1&&std::any_of(boxes->begin(),boxes->end(),[&](const auto& b){return _wcsicmp(b.c_str(),it->second.box.c_str())==0;});
}
