using Microsoft.AspNetCore.SignalR;

namespace Metrics_Dashboard.Hubs;

/// <summary>
/// Hub delgado: el push real lo hace MetricsBroadcastService (BackgroundService).
/// - Dashboard general -> grupo "General" (se une automático al conectar).
/// - Dashboards de horno/Tube Mills -> grupo "Furnace-{id}" (se unen explícitamente
///   llamando a JoinFurnace(id) desde el cliente, id 1..5 = hornos, 6 = Tube Mills).
/// </summary>
public class MetricsHub : Hub
{
    public const string GeneralGroup = "General";

    public static string FurnaceGroup(int furnaceId) => $"Furnace-{furnaceId}";

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

    /// <summary>Llamado desde el JS de cada dashboard de horno para recibir sus propias actualizaciones.</summary>
    public async Task JoinFurnace(int furnaceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, FurnaceGroup(furnaceId));
    }
}
