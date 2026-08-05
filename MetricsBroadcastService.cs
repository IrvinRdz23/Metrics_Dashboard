using Metrics_Dashboard.Hubs;
using Metrics_Dashboard.Models;
using Microsoft.AspNetCore.SignalR;

namespace Metrics_Dashboard.Services;

/// <summary>
/// Único punto que "toca" la base de datos por tiempo. Llama al SP UNA vez por intervalo
/// (vía IMetricsRawDataService) y de esa misma lectura arma y transmite:
///   - el snapshot general (grupo "General")
///   - los 6 snapshots de detalle: Furnace 1..5 + Tube Mills (grupo "Furnace-{id}" cada uno)
///   - los N snapshots de línea individual, uno por cada Product_List_ID visto en esta
///     lectura (grupo "Line-{id}" cada uno)
/// No importa cuántas pantallas estén abiertas (general + hornos + 53 líneas a la vez):
/// el SP se ejecuta una sola vez por ciclo, sin importar cuántos dashboards lo consuman.
/// </summary>
public class MetricsBroadcastService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<MetricsHub> _hubContext;
    private readonly ILogger<MetricsBroadcastService> _logger;
    private readonly TimeSpan _interval;

    public MetricsBroadcastService(
        IServiceProvider serviceProvider,
        IHubContext<MetricsHub> hubContext,
        IConfiguration config,
        ILogger<MetricsBroadcastService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _logger = logger;

        var seconds = config.GetValue<int>("PlantMetrics:PollingIntervalSeconds", 60);
        _interval = TimeSpan.FromSeconds(Math.Max(10, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);

        await PushAllSnapshotsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PushAllSnapshotsAsync(stoppingToken);
        }
    }

    private async Task PushAllSnapshotsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var rawDataService = scope.ServiceProvider.GetRequiredService<IMetricsRawDataService>();
            var plantService = scope.ServiceProvider.GetRequiredService<IPlantMetricsService>();
            var furnaceService = scope.ServiceProvider.GetRequiredService<IFurnaceDetailService>();
            var lineService = scope.ServiceProvider.GetRequiredService<ILineDetailService>();

            // Una sola llamada al SP para todo este ciclo, sin importar cuántos
            // dashboards distintos (general + hornos + líneas) estén conectados.
            var (rows, shiftDesc) = await rawDataService.FetchCurrentShiftRowsAsync(ct);

            var general = plantService.BuildFromRows(rows, shiftDesc);
            await _hubContext.Clients.Group(MetricsHub.GeneralGroup)
                .SendAsync("SnapshotUpdated", general, cancellationToken: ct);

            foreach (var furnaceId in FurnaceCatalog.Map.Keys)
            {
                var detail = furnaceService.BuildFromRows(rows, shiftDesc, furnaceId);
                await _hubContext.Clients.Group(MetricsHub.FurnaceGroup(furnaceId))
                    .SendAsync("FurnaceSnapshotUpdated", detail, cancellationToken: ct);
            }

            // Un snapshot por cada línea real vista en esta lectura (típicamente ~53).
            var lineIds = rows.BuildProductListIdLookup().Values.Distinct();
            foreach (var lineId in lineIds)
            {
                var lineDetail = lineService.BuildFromRows(rows, shiftDesc, lineId);
                await _hubContext.Clients.Group(MetricsHub.LineGroup(lineId))
                    .SendAsync("LineSnapshotUpdated", lineDetail, cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transmitiendo snapshots por SignalR");
        }
    }
}
