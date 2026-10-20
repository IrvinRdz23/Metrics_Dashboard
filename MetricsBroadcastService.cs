using Metrics_Dashboard.Hubs;
using Metrics_Dashboard.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Metrics_Dashboard.Services;

/// <summary>
/// Cada RefreshSeconds empuja el snapshot del dashboard general a todos los clientes conectados
/// ("SnapshotUpdated"). Si no hay nadie conectado no golpea el SP. Los detalles de grupo y las TVs
/// de línea no usan SignalR: refrescan con fetch a /Group/{id}/Data y /Line/{id}/Data.
/// </summary>
public class MetricsBroadcastService : BackgroundService
{
    private readonly IPlantMetricsService _plant;
    private readonly IHubContext<MetricsHub> _hub;
    private readonly PlantOptions _options;
    private readonly ILogger<MetricsBroadcastService> _logger;

    public MetricsBroadcastService(IPlantMetricsService plant, IHubContext<MetricsHub> hub,
        IOptions<PlantOptions> options, ILogger<MetricsBroadcastService> logger)
    {
        _plant = plant;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(5, _options.RefreshSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (MetricsHub.ConnectedClients > 0)
                {
                    var snapshot = await _plant.GetSnapshotAsync(stoppingToken);
                    await _hub.Clients.All.SendAsync("SnapshotUpdated", snapshot, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en el ciclo de broadcast");
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
