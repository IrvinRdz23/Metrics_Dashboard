using Microsoft.AspNetCore.SignalR;

namespace PlantMetricsDashboard.Hubs;

/// <summary>
/// Hub muy delgado: el push real de datos lo hace MetricsBroadcastService
/// (BackgroundService). El hub solo existe para que los clientes se conecten
/// al grupo "General" y reciban el evento "SnapshotUpdated".
/// </summary>
public class MetricsHub : Hub
{
    public const string GeneralGroup = "General";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GeneralGroup);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GeneralGroup);
        await base.OnDisconnectedAsync(exception);
    }
}
