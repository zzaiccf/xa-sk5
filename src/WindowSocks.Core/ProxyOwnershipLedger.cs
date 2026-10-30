namespace WindowSocks.Core;

// One reservation identity per target. A stopped sandbox/fixed VM still owns its
// configured line; process liveness does not turn a persistent reservation into a free slot.
public sealed class ProxyOwnershipLedger
{
    public InstanceBinding[] Entries { get; }
    public ProxyOwnershipLedger(IEnumerable<InstanceBinding> entries) => Entries=entries.Where(e=>e.ProxyId!=Guid.Empty).GroupBy(e=>e.TargetKey,StringComparer.OrdinalIgnoreCase).Select(g=>g.Last()).ToArray();
    public static ProxyOwnershipLedger From(SavedConfig config,IEnumerable<VmwareRuntime>? runtime=null)
    {
        var entries=config.Bindings.Select(b=>b with{}).ToList();
        entries.AddRange(config.SandboxInstances.Select(p=>new InstanceBinding{TargetKey=p.TargetKey,Kind="Sandbox",ProxyId=p.ProxyId}));
        if(!config.Vmware.ByStartup) entries.AddRange(config.Vmware.Machines.Where(m=>!m.UseLocalNetwork).Select(m=>Vm(m.VmxPath,m.ProxyId)));
        entries.AddRange((runtime??config.VmwareSessions).Where(r=>!r.UseLocalNetwork&&r.ProxyId!=Guid.Empty).Select(r=>Vm(r.VmxPath,r.ProxyId)));
        if(config.GlobalProxy.Enabled)entries.Add(new(){TargetKey="global",Kind="Global",ProxyId=config.GlobalProxy.ProxyId});
        return new(entries);
    }
    private static InstanceBinding Vm(string path,Guid proxy)=>new(){TargetKey="vmware:"+VmwareVmx.NormalizePath(path),Kind="VMware",ProxyId=proxy,SharedProxy=true};
    public InstanceBinding[] Outside(string kind)=>Entries.Where(e=>e.Kind!=kind).Select(e=>e with{SharedProxy=false}).ToArray();
    public void Require(Guid proxy,string owner,string kind,bool shared=false)
    {
        if(Entries.Any(e=>e.ProxyId==proxy&&!e.TargetKey.Equals(owner,StringComparison.OrdinalIgnoreCase)&&(!shared||!e.SharedProxy||e.Kind!=kind)))
            throw new InvalidOperationException("此線路已由其他目標保留，不能重複分配。");
    }
    public bool Conflicted(Guid id)
    {
        var uses=Entries.Where(e=>e.ProxyId==id).ToArray();
        return uses.Length>1&&(uses.Any(e=>!e.SharedProxy)||uses.Select(e=>e.Kind).Distinct().Count()>1);
    }
    public bool DuplicateExit(Guid id,IReadOnlyDictionary<Guid,ProbeResult> probes)=>Conflicted(id)||AssignmentPlanner.HasDuplicateExit(id,Entries,probes);
    public void ValidateBindings(IEnumerable<InstanceBinding> bindings)
    {
        var proposed=bindings.ToArray();var keys=proposed.Select(b=>b.TargetKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next=new ProxyOwnershipLedger(Entries.Where(e=>!keys.Contains(e.TargetKey)).Concat(proposed));
        if(next.Entries.Any(e=>next.Conflicted(e.ProxyId)))throw new InvalidOperationException("線路保留衝突，請先解除其他目標的占用。");
    }
}
