using Microsoft.AspNetCore.SignalR;

namespace Metrics_Dashboard.Hubs;

/// <summary>Hub vacío: el servidor solo empuja "SnapshotUpdated". Cuenta clientes para no consultar el SP en balde.</summary>
public class MetricsHub : Hub
{
    private static int _connected;
    public static int ConnectedClients => Volatile.Read(ref _connected);

    public override Task OnConnectedAsync()
    {
        Interlocked.Increment(ref _connected);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Interlocked.Decrement(ref _connected);
        return base.OnDisconnectedAsync(exception);
    }
}
