namespace WindowSocks.Core;

// Runtime state only: a service restart starts a new routing session.
public sealed class GlobalProxySessionTracker
{
    private Guid? activeProxy;
    public DateTimeOffset? ConnectedAt { get; private set; }

    public void Synchronize(Guid? routingProxy, DateTimeOffset now)
    {
        if (routingProxy is null) { activeProxy = null; ConnectedAt = null; return; }
        if (activeProxy != routingProxy) { activeProxy = routingProxy; ConnectedAt = now; }
    }
}
