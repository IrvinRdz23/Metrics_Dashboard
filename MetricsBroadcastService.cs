using Microsoft.AspNetCore.SignalR;
using PlantMetricsDashboard.Hubs;

namespace PlantMetricsDashboard.Services;

/// <summary>
/// Único punto que "toca" la base de datos por tiempo. Corre en segundo plano,
/// llama a IPlantMetricsService cada N segundos (appsettings:PlantMetrics:PollingIntervalSeconds)
/// y transmite el snapshot a todos los clientes conectados al hub.
///
/// Esto evita que cada navegador abierto dispare su propia consulta al SP:
/// no importa si hay 1 o 20 pantallas viendo el dashboard, el SP se llama
/// una sola vez por intervalo.
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

        // Primer push inmediato al arrancar, sin esperar el primer tick.
        await PushSnapshotAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PushSnapshotAsync(stoppingToken);
        }
    }

    private async Task PushSnapshotAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var metricsService = scope.ServiceProvider.GetRequiredService<IPlantMetricsService>();

            var snapshot = await metricsService.GetSnapshotAsync(ct);

            await _hubContext.Clients.Group(MetricsHub.GeneralGroup)
                .SendAsync("SnapshotUpdated", snapshot, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transmitiendo snapshot por SignalR");
        }
    }
}
