// One bounded queue per direction. A slow writer must not prevent reverse reads.
// No extra thread, unbounded buffering, or timeout-driven reconnect is introduced.
struct TcpRelayBuffer {
    std::array<char,65536> bytes{};
    int begin=0,end=0;
    bool readOpen=true,sendClosed=false;
    uint64_t queuedAt=0,lastProgress=0;
    bool empty()const{return begin==end;}
    bool done()const{return !readOpen&&empty();}
    void queued(int size){begin=0;end=size;queuedAt=qpcUs();lastProgress=GetTickCount64();}
    void record(){if(!queuedAt)return;uint64_t delay=elapsedSince(qpcUs(),queuedAt);++packetMetrics.tcpQueueSamples;packetMetrics.tcpQueueUs+=delay;peak(packetMetrics.tcpQueueMaxUs,delay);queuedAt=0;}
    void consumed(int size){begin+=size;lastProgress=GetTickCount64();if(empty()){record();begin=end=0;}}
    ~TcpRelayBuffer(){record();}
};
static uint32_t tcpPump(std::shared_ptr<Flow> f,SOCKET accepted,SOCKET remote,bool managed,uint32_t& error) {
    configure_tcp_socket(accepted,65536,5000);configure_tcp_socket(remote,65536,5000);
    u_long nonblocking=1;
    if(ioctlsocket(accepted,FIONBIO,&nonblocking)||ioctlsocket(remote,FIONBIO,&nonblocking)){error=WSAGetLastError();return SelectFailed;}
    f->connected=true;trace(*f,Connected);
    TcpRelayBuffer upload,download;
    auto receive=[&](SOCKET source,TcpRelayBuffer& buffer,uint32_t eof,uint32_t failure)->uint32_t {
        int n=recv(source,buffer.bytes.data(),(int)buffer.bytes.size(),0);
        if(n>0){buffer.queued(n);f->last=GetTickCount64();return 0;}
        if(n==0){buffer.readOpen=false;f->connected=false;trace(*f,eof);return 0;}
        uint32_t code=WSAGetLastError();if(code==WSAEWOULDBLOCK)return 0;
        error=code;return failure;
    };
    auto transmit=[&](SOCKET destination,TcpRelayBuffer& buffer,bool sending)->uint32_t {
        if(buffer.empty())return 0;
        int n=send(destination,buffer.bytes.data()+buffer.begin,buffer.end-buffer.begin,0);
        if(n>0){
            buffer.consumed(n);f->last=GetTickCount64();
            if(sending){f->sent+=n;count(f->rule.pid,n,0,0,f->rule.created);}
            else{f->received+=n;count(f->rule.pid,0,n,0,f->rule.created);}
            return 0;
        }
        uint32_t code=n==0?WSAECONNRESET:WSAGetLastError();
        if(code==WSAEWOULDBLOCK){++packetMetrics.tcpWouldBlock;return 0;}
        error=code;return sending?SendToProxyFailed:SendToClientFailed;
    };
    while((active||!managed)&&f->alive&&(!managed||stillBound(*f))&&(!upload.done()||!download.done())) {
        // Preserve the prior five-second no-progress send limit, independently
        // for each direction. An idle but empty queue has no send deadline.
        uint64_t now=GetTickCount64();
        if((!upload.empty()&&elapsedSince(now,upload.lastProgress)>=5000)||(!download.empty()&&elapsedSince(now,download.lastProgress)>=5000)){
            ++packetMetrics.tcpStallTimeouts;error=WSAETIMEDOUT;
            return !upload.empty()&&elapsedSince(now,upload.lastProgress)>=5000?SendToProxyFailed:SendToClientFailed;
        }
        fd_set read,write;FD_ZERO(&read);FD_ZERO(&write);
        if(upload.readOpen&&upload.empty())FD_SET(accepted,&read);
        if(download.readOpen&&download.empty())FD_SET(remote,&read);
        if(!upload.empty())FD_SET(remote,&write);
        if(!download.empty())FD_SET(accepted,&write);
        timeval timeout{0,200000};int ready=select(0,&read,&write,nullptr,&timeout);
        if(ready<0){error=WSAGetLastError();return SelectFailed;}if(!ready)continue;
        // A policy cancellation during select must not admit another chunk.
        if(!f->alive||(managed&&(!active||!stillBound(*f))))break;
        uint32_t reason;
        if(FD_ISSET(accepted,&read)&&(reason=receive(accepted,upload,ClientEof,ClientReadFailed)))return reason;
        if(FD_ISSET(remote,&read)&&(reason=receive(remote,download,UpstreamEof,UpstreamReadFailed)))return reason;
        // Try newly read bytes immediately; nonblocking send never waits for a
        // whole 64-KiB chunk. Partial writes retain order until select signals.
        if((reason=transmit(remote,upload,true)))return reason;
        if((reason=transmit(accepted,download,false)))return reason;
        if(upload.done()&&!upload.sendClosed){shutdown(remote,SD_SEND);upload.sendClosed=true;}
        if(download.done()&&!download.sendClosed){shutdown(accepted,SD_SEND);download.sendClosed=true;}
    }
    return !managed||(upload.done()&&download.done())?Closed:active?PolicyClosed:EngineStopped;
}
