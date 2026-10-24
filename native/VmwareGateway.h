// Dedicated IPv4 host-only VMnet gateway. No guest agent or VMware NAT process routing.
struct GatewayRule {uint32_t source,pid,proxy,blocked;uint64_t created;};
static std::vector<GatewayRule> gatewayRules;
static uint32_t gatewayNetwork=0,gatewayMask=0,gatewayHost=0,gatewayInterface=0;
static std::atomic<bool> gatewayActive{false};
static HANDLE gatewayForward=INVALID_HANDLE_VALUE,gatewayInbound=INVALID_HANDLE_VALUE;
static std::thread gatewayForwardThread,gatewayInboundThread;
static std::mutex gatewayLifecycle;
static bool gatewayDhcpResponse(const Key& key,uint32_t index) {
    if(!gatewayActive||key.protocol!=17||key.sp!=67||key.dp!=68)return false;
    std::shared_lock lk(stateMutex);
    return index==gatewayInterface&&(key.src&gatewayMask)==gatewayNetwork&&((key.dst&gatewayMask)==gatewayNetwork||key.dst==INADDR_BROADCAST);
}
static void redirectGuestTcp(Flow& f,std::vector<uint8_t>& p,WINDIVERT_ADDRESS& addr,uint16_t port) {
    int offset=(p[0]&15)*4;memcpy(p.data()+16,&f.gatewayHost,4);put16(p.data()+offset,f.alias);put16(p.data()+offset+2,port);
    addr.Layer=WINDIVERT_LAYER_NETWORK;addr.Outbound=0;addr.Loopback=0;addr.Impostor=0;addr.Network.IfIdx=f.gatewayInterface;addr.Network.SubIfIdx=0;
}
static int gatewayRoute(uint32_t source,RouteRule& output) {
    std::shared_lock lk(stateMutex);
    for(const auto& r:gatewayRules)if(r.source==source){output={r.pid,r.proxy,r.created,r.blocked,0};
        if(!r.pid||!r.created||created(r.pid)!=r.created)return 2;
        if(r.blocked==2&&r.proxy==0)return 3; // Explicit host-network relay, never a proxy failure fallback.
        return !r.blocked&&profiles.count(r.proxy)?1:2;}
    return 2; // Every unassigned source on the dedicated subnet stays quarantined.
}
static bool gatewayStillBound(const Flow& f){RouteRule r{};int action=gatewayRoute(f.key.src,r);return gatewayActive&&(action==1||action==3)&&r.pid==f.rule.pid&&r.created==f.rule.created&&r.proxy==f.rule.proxy;}
static void gatewayCapture(HANDLE handle,bool inbound) {
    while(active&&gatewayActive) {
        std::vector<uint8_t> p(65535);UINT length=0;WINDIVERT_ADDRESS addr{};
        if(!wdRecv(handle,p.data(),(UINT)p.size(),&length,&addr)){if(gatewayActive)lastError=GetLastError();break;}
        p.resize(length);if(length<20)continue;
        if((p[0]>>4)!=4)continue;
        int offset=(p[0]&15)*4;if(offset<20||offset+8>(int)length||be16(p.data()+6)&0x3fff)continue;
        uint8_t protocol=p[9];
        // DHCP stays with VMware's DHCP server. Other local host services are not exposed.
        if(inbound&&protocol==17&&be16(p.data()+offset)==68&&be16(p.data()+offset+2)==67){std::lock_guard lk(sendMutex);wdSend(handle,p.data(),length,nullptr,&addr);continue;}
        if(protocol!=6&&protocol!=17)continue;
        Key key{word(p.data()+12),word(p.data()+16),be16(p.data()+offset),be16(p.data()+offset+2),protocol};
        uint32_t host,index,network,mask;{std::shared_lock lk(stateMutex);host=gatewayHost;index=gatewayInterface;network=gatewayNetwork;mask=gatewayMask;}
        if((key.src&mask)!=network||key.src==host)continue;
        // Local ingress only proxies DNS; all other traffic must arrive through the forwarding layer.
        if(inbound&&(key.dst!=host||key.dp!=53))continue;
        if((ntohl(key.dst)&0xf0000000)==0xe0000000||key.dst==INADDR_BROADCAST||key.dst==(network|~mask))continue;
        RouteRule rule{};int action=gatewayRoute(key.src,rule);if(action!=1&&action!=3){count(rule.pid,0,0,length,rule.created);continue;}
        std::shared_ptr<Flow> f;bool fresh=false;
        {std::lock_guard lk(flowMutex);auto it=flows.find(key);
            if(it!=flows.end()){f=it->second;if(!f->gatewayInterface||!stillBound(*f)||(!f->alive&&GetTickCount64()-f->last>1000)){f->close();if(f->alias)aliases.erase(f->alias);flows.erase(it);f.reset();}}
            if(!f){
                if(flows.size()>=2048||workers>=256)continue;
                if(protocol==6&&(offset+20>(int)length||!(p[offset+13]&2)))continue;
                f=std::make_shared<Flow>();f->key=key;f->rule=rule;f->gatewayHost=host;f->gatewayInterface=index;f->address=addr;
                if(protocol==6){for(unsigned n=0;n<40000;n++){if(++nextAlias>60000)nextAlias=20000;if(!aliases.count(nextAlias)){f->alias=nextAlias;break;}}if(!f->alias)continue;aliases[f->alias]=f;}
                flows[key]=f;trace(*f,Captured);fresh=true;
            }f->last=GetTickCount64();
        }
        if(!f->alive)continue;
        if(protocol==6){redirectGuestTcp(*f,p,addr,listenPort);inject(p,addr);}
        else {size_t size=be16(p.data()+offset+4);if(size<8||offset+size>length)continue;{std::lock_guard lk(f->queueMutex);if(f->queue.size()<128)f->queue.emplace_back(p.begin()+offset+8,p.begin()+offset+size);}if(fresh)spawn([f](){udpWorker(f);});}
    }
}
API int Core_ReplaceGatewayRules(const GatewayRule* entries,uint32_t countRules) {
    if(countRules>10||(!entries&&countRules))return 0;std::vector<GatewayRule> next;if(countRules)next.assign(entries,entries+countRules);
    std::set<uint32_t> sources;for(const auto& r:next)if(!r.source||r.blocked>2||!sources.insert(r.source).second||(r.blocked!=1&&(!r.pid||!r.created))||(!r.blocked&&!r.proxy)||(r.blocked==2&&r.proxy))return 0;
    {std::unique_lock lk(stateMutex);gatewayRules.swap(next);}
    std::lock_guard lk(flowMutex);for(auto& [key,f]:flows)if(f->gatewayInterface&&!stillBound(*f))f->close(PolicyClosed);return 1;
}
API int Core_GatewayRouteForTest(uint32_t source,uint32_t* proxy){RouteRule r{};int result=gatewayRoute(source,r);if(proxy)*proxy=r.proxy;return result;}
API int Core_GatewayLocalSupported(){return 1;}
API SOCKET Core_GatewayOpenLocalForTest(uint32_t destination,uint16_t port,int udp){return (udp==0||udp==1)&&port?openLocal(destination,port,udp!=0):INVALID_SOCKET;}
API uint32_t Core_GatewayDestinationForTest(uint32_t host,uint32_t destination,uint16_t port,int local){Flow f;f.alive=false;f.gatewayInterface=1;f.gatewayHost=host;f.rule.proxy=local?0:1;f.key.dst=destination;f.key.dp=port;return relayDestination(f);}
API int Core_GatewayTranslationForTest(uint8_t* packet,uint32_t length,uint32_t guest,uint32_t destination,uint32_t host,uint32_t index,int reply,uint32_t* outbound) {
    if(!packet||length<40||(packet[0]>>4)!=4||(packet[0]&15)!=5||packet[9]!=6)return 0;
    Flow f;f.key={guest,destination,41000,443,6};f.gatewayHost=host;f.gatewayInterface=index;f.alias=25000;WINDIVERT_ADDRESS addr{};
    std::vector<uint8_t> bytes(packet,packet+length);if(reply)reverseTcp(f,bytes,addr);else redirectGuestTcp(f,bytes,addr,18000);
    memcpy(packet,bytes.data(),length);if(outbound)*outbound=addr.Outbound;return addr.Network.IfIdx==index?1:0;
}
API int Core_StartGateway(uint32_t network,uint32_t mask,uint32_t host,uint32_t index) {
    std::lock_guard mainLifecycle(lifecycleMutex);
    std::lock_guard lk(gatewayLifecycle);
    if(!active||!index||mask!=inet_addr("255.255.255.0")||(host&mask)!=network||host==network||host==(network|~mask))return 0;
    if(gatewayActive){std::shared_lock state(stateMutex);return network==gatewayNetwork&&host==gatewayHost&&index==gatewayInterface?1:0;}
    char begin[INET_ADDRSTRLEN],end[INET_ADDRSTRLEN];IN_ADDR a{},b{};a.s_addr=network;b.s_addr=network|~mask;InetNtopA(AF_INET,&a,begin,sizeof(begin));InetNtopA(AF_INET,&b,end,sizeof(end));
    std::string filter="ip and ip.SrcAddr > "+std::string(begin)+" and ip.SrcAddr < "+end;
    gatewayForward=wdOpen(filter.c_str(),WINDIVERT_LAYER_NETWORK_FORWARD,110,0);if(gatewayForward==INVALID_HANDLE_VALUE){lastError=GetLastError();return 0;}
    filter="inbound and ifIdx == "+std::to_string(index)+" and (ip or ipv6)";
    gatewayInbound=wdOpen(filter.c_str(),WINDIVERT_LAYER_NETWORK,110,0);if(gatewayInbound==INVALID_HANDLE_VALUE){lastError=GetLastError();wdClose(gatewayForward);gatewayForward=INVALID_HANDLE_VALUE;return 0;}
    {std::unique_lock state(stateMutex);gatewayNetwork=network;gatewayMask=mask;gatewayHost=host;gatewayInterface=index;}
    gatewayActive=true;gatewayForwardThread=std::thread([]{try{gatewayCapture(gatewayForward,false);}catch(...){lastError=ERROR_UNHANDLED_EXCEPTION;}});gatewayInboundThread=std::thread([]{try{gatewayCapture(gatewayInbound,true);}catch(...){lastError=ERROR_UNHANDLED_EXCEPTION;}});return 1;
}
static void stopGateway(){std::lock_guard lk(gatewayLifecycle);if(!gatewayActive.exchange(false))return;
    wdShutdown(gatewayForward,WINDIVERT_SHUTDOWN_RECV);wdShutdown(gatewayInbound,WINDIVERT_SHUTDOWN_RECV);
    if(gatewayForwardThread.joinable())gatewayForwardThread.join();if(gatewayInboundThread.joinable())gatewayInboundThread.join();
    {std::lock_guard flow(flowMutex);for(auto& [key,f]:flows)if(f->gatewayInterface)f->close(PolicyClosed);}
    wdClose(gatewayForward);wdClose(gatewayInbound);gatewayForward=gatewayInbound=INVALID_HANDLE_VALUE;
}
API void Core_StopGateway(){stopGateway();}
API int Core_GatewayRunning(){return gatewayActive&&active&&lastError==0?1:0;}
