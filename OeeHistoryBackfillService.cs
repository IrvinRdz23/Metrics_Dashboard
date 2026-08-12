namespace Metrics_Dashboard.Services;

/// <summary>
/// Corre en segundo plano, sin bloquear nada:
///   1) Rellena el historial VIEJO poco a poco (20 dias por tanda, cada 2 minutos) hasta
///      llegar a ~30 dias seguidos sin datos (ahi asumimos que se acabo el historial real)
///      o hasta un tope de seguridad de ~4 anos.
///   2) De ahi en adelante, revisa que "ayer" siempre quede guardado - por si la app se
///      reinicio o el backfill nunca corrio.
/// El progreso se guarda en PlantMetrics_OeeHistory_BackfillState, asi que si la app se
/// reinicia a medio backfill, sigue donde se quedo en vez de empezar de cero.
/// </summary>
public class OeeHistoryBackfillService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OeeHistoryBackfillService> _logger;

    private const int MaxConsecutiveEmptyDays = 30;
    private const int MaxBackfillDays = 1500; // ~4 anos, tope de seguridad
    private const int DaysPerBatch = 20;
    private static readonly TimeSpan DelayBetweenBatches = TimeSpan.FromMinutes(2);

    public OeeHistoryBackfillService(IServiceProvider serviceProvider, ILogger<OeeHistoryBackfillService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunBackfillBatchAsync(stoppingToken);
                await EnsureYesterdayStoredAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en OeeHistoryBackfillService");
            }

            try { await Task.Delay(DelayBetweenBatches, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task RunBackfillBatchAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IOeeHistoryStorageService>();
        var rawData = scope.ServiceProvider.GetRequiredService<IMetricsRawDataService>();

        var (oldestDate, consecutiveEmpty, isComplete) = await storage.GetBackfillStateAsync(ct);
        if (isComplete) return;

        var cursor = oldestDate?.AddDays(-1) ?? DateTime.Today.AddDays(-1);
        int processedThisBatch = 0;

        while (processedThisBatch < DaysPerBatch && !ct.IsCancellationRequested)
        {
            if (!await storage.IsDayStoredAsync(cursor, ct))
            {
                var rows = await rawData.FetchDayRowsAsync(cursor, ct);
                var hasData = rows.Any(r => r.ReportGroup == 1 && r.PlannedForOee != 0);

                if (hasData)
                {
                    await storage.UpsertDayAsync(cursor, rows, ct);
                    consecutiveEmpty = 0;
                }
                else
                {
                    consecutiveEmpty++;
                }
            }

            var daysBackSoFar = (DateTime.Today - cursor).Days;
            var stop = consecutiveEmpty >= MaxConsecutiveEmptyDays || daysBackSoFar >= MaxBackfillDays;

            await storage.UpdateBackfillProgressAsync(cursor, consecutiveEmpty, stop, ct);

            if (stop)
            {
                _logger.LogInformation(
                    "Backfill de OEE historico terminado. Llego hasta {Date} ({Reason}).",
                    cursor, consecutiveEmpty >= MaxConsecutiveEmptyDays ? "sin mas datos atras" : "tope de seguridad");
                return;
            }

            cursor = cursor.AddDays(-1);
            processedThisBatch++;
        }

        _logger.LogInformation("Backfill de OEE historico: tanda de {N} dias lista, va en {Date}.", processedThisBatch, cursor);
    }

    private async Task EnsureYesterdayStoredAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IOeeHistoryStorageService>();
        var rawData = scope.ServiceProvider.GetRequiredService<IMetricsRawDataService>();

        var yesterday = DateTime.Today.AddDays(-1);
        if (await storage.IsDayStoredAsync(yesterday, ct)) return;

        var rows = await rawData.FetchDayRowsAsync(yesterday, ct);
        if (rows.Any(r => r.ReportGroup == 1 && r.PlannedForOee != 0))
        {
            await storage.UpsertDayAsync(yesterday, rows, ct);
            _logger.LogInformation("Guardado automatico de ayer ({Date}) en PlantMetrics_OeeHistory.", yesterday);
        }
    }
}
