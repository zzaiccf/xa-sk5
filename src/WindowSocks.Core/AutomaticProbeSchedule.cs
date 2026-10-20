namespace WindowSocks.Core;

public readonly record struct ProbeJob(Guid ProxyId, int Generation);

// UI-independent scheduling: repeated snapshots never duplicate work, and a new backend is a new session.
public sealed class AutomaticProbeSchedule
{
    private readonly LinkedList<Guid> pending = [];
    private readonly HashSet<Guid> queued = [];
    private readonly Dictionary<Guid, DateTimeOffset> nextChecks = [];
    private HashSet<Guid> available = [];
    private string? source;
    private int generation;
    public Guid? Active { get; private set; }
    public bool Paused { get; private set; }
    public int PendingCount => pending.Count;
    public bool IsQueued(Guid id) => queued.Contains(id);
    public bool IsCurrent(ProbeJob job) => job.Generation == generation && Active == job.ProxyId;

    public bool Synchronize(string backend, IEnumerable<Guid> ids, DateTimeOffset now)
    {
        var current = ids.Distinct().ToArray();
        bool changed = source != backend;
        if (changed) {
            source = backend; generation++; pending.Clear(); queued.Clear(); nextChecks.Clear(); Active = null; Paused = false;
        }
        available = current.ToHashSet();
        foreach (var id in queued.Where(id => !available.Contains(id)).ToArray()) { pending.Remove(id); queued.Remove(id); }
        foreach (var id in nextChecks.Keys.Where(id => !available.Contains(id)).ToArray()) nextChecks.Remove(id);
        bool cancelActive = Active is { } active && !available.Contains(active);
        if (cancelActive) Active = null;
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
        if (!available.Contains(id) || Active == id) return;
        if (!queued.Add(id)) { if (!priority) return; pending.Remove(id); }
        if (priority) pending.AddFirst(id); else pending.AddLast(id);
    }
    public bool TryStart(out ProbeJob job)
    {
        job = default;
        // While paused only explicit requests/imports can enqueue work; manual checks remain available.
        if (Active is not null || pending.First is null) return false;
        Guid id = pending.First.Value; pending.RemoveFirst(); queued.Remove(id); Active = id;
        job = new(id, generation); return true;
    }
    public void Complete(ProbeJob job, bool ready, DateTimeOffset now)
    {
        if (!IsCurrent(job)) return;
        Active = null;
        if (available.Contains(job.ProxyId)) nextChecks[job.ProxyId] = now.Add(ProbeTiming.NextInterval(ready));
    }
    public void Stop()
    {
        Paused = true; pending.Clear(); queued.Clear();
    }
    public void Resume() => Paused = false;
}
