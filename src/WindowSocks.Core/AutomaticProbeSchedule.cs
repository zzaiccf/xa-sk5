namespace WindowSocks.Core;

public readonly record struct ProbeJob(Guid ProxyId, int Generation, long Sequence = 0);

// UI-independent scheduling: repeated snapshots never duplicate work, and a new backend is a new session.
public sealed class AutomaticProbeSchedule
{
    private readonly LinkedList<Guid> pending = [];
    private readonly HashSet<Guid> queued = [];
    private readonly Dictionary<Guid, DateTimeOffset> nextChecks = [];
    private readonly Dictionary<Guid, Guid> supplements = [];
    private HashSet<Guid> available = [];
    private string? source;
    private int generation;
    private long sequence;
    private readonly Dictionary<Guid, ProbeJob> active = [];
    public int MaxConcurrent { get; }
    public AutomaticProbeSchedule(int maxConcurrent = ProbeTiming.MaxConcurrentProbes)
    {
        if (maxConcurrent is < 1 or > ProbeTiming.MaxConcurrentProbes) throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        MaxConcurrent = maxConcurrent;
    }
    public int ActiveCount => active.Count;
    public bool IsActive(Guid id) => active.ContainsKey(id);
    public bool Paused { get; private set; }
    public int PendingCount => pending.Count;
    public bool IsQueued(Guid id) => queued.Contains(id);
    public bool IsCurrent(ProbeJob job) => job.Generation == generation && active.TryGetValue(job.ProxyId, out var current) && current == job;

    public bool Synchronize(string backend, IEnumerable<Guid> ids, DateTimeOffset now)
    {
        var current = ids.Distinct().ToArray();
        bool changed = source != backend;
        if (changed) {
            source = backend; generation++; pending.Clear(); queued.Clear(); nextChecks.Clear(); supplements.Clear(); active.Clear(); Paused = false;
        }
        available = current.ToHashSet();
        foreach (var id in queued.Where(id => !available.Contains(id)).ToArray()) { pending.Remove(id); queued.Remove(id); }
        foreach (var id in nextChecks.Keys.Where(id => !available.Contains(id)).ToArray()) nextChecks.Remove(id);
        foreach (var id in supplements.Keys.Where(id => !available.Contains(id)).ToArray()) supplements.Remove(id);
        bool cancelActive = false;
        foreach (var id in active.Keys.Where(id => !available.Contains(id)).ToArray()) { active.Remove(id); cancelActive = true; }
        if (!Paused) foreach (var id in current)
            if (!nextChecks.TryGetValue(id, out var due) || due <= now) Enqueue(id, false);
        return changed || cancelActive;
    }

    public void Request(IEnumerable<Guid> ids)
    {
        // Imports and explicit checks go ahead of the remaining startup batch.
        foreach (var id in ids.Distinct().Reverse()) Enqueue(id, true);
    }
    private void Enqueue(Guid id, bool priority)
    {
        if (!available.Contains(id) || active.ContainsKey(id)) return;
        if (!queued.Add(id)) { if (!priority) return; pending.Remove(id); }
        if (priority) pending.AddFirst(id); else pending.AddLast(id);
    }
    public void Invalidate(Guid id)
    {
        active.Remove(id); supplements.Remove(id); nextChecks.Remove(id);
        pending.Remove(id); queued.Remove(id);
    }
    public bool TryStart(out ProbeJob job)
    {
        job = default;
        // While paused only explicit requests/imports can enqueue work; manual checks remain available.
        if (active.Count >= MaxConcurrent || pending.First is null) return false;
        Guid id = pending.First.Value; pending.RemoveFirst(); queued.Remove(id);
        job = new(id, generation, ++sequence); active.Add(id, job); return true;
    }
    public void Complete(ProbeJob job, bool ready, DateTimeOffset now, Guid checkId = default, bool supplementalPending = false)
    {
        if (!IsCurrent(job)) return;
        active.Remove(job.ProxyId);
        supplements.Remove(job.ProxyId);
        if (supplementalPending && checkId != Guid.Empty) supplements[job.ProxyId] = checkId;
        if (available.Contains(job.ProxyId)) nextChecks[job.ProxyId] = now.Add(ProbeTiming.NextInterval(ready));
    }
    public void ObserveResults(IEnumerable<ProbeResult> results)
    {
        foreach (var result in results) {
            if (result.UdpPending || active.ContainsKey(result.ProxyId) || queued.Contains(result.ProxyId) ||
                !supplements.TryGetValue(result.ProxyId, out var id) || id != result.CheckId) continue;
            nextChecks[result.ProxyId] = result.At.Add(ProbeTiming.NextInterval(result.Ready));
            supplements.Remove(result.ProxyId);
        }
    }
    public void Stop()
    {
        Paused = true; pending.Clear(); queued.Clear();
    }
    public void Resume() => Paused = false;
}
