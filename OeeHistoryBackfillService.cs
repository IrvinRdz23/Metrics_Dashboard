namespace Metrics_Dashboard.Services;

/// <summary>
/// Corre en segundo plano, sin bloquear nada:
///   1) Rellena el historial VIEJO poco a poco (20 dias por tanda, cada 2 minutos) hasta
///      llegar a ~30 dias seguidos sin datos (ahi asumimos que se acabo el historial real)
///      o hasta un tope de seguridad de ~4 anos. SOLO corre si IsComplete=0 — esto es lo
///      que EXTIENDE el rango hacia atras, nunca lo usa para rellenar huecos.
///   2) Rellena HUECOS dentro del rango que ya se cubrio (entre OldestDateBackfilled y
///      ayer) — dias que por lo que sea nunca quedaron marcados. Este SIEMPRE corre, sin
///      importar IsComplete, y NUNCA va mas atras de OldestDateBackfilled. Es a proposito
///      un proceso separado del punto 1: pausar el backfill (IsComplete=1) no debe frenar
///      el relleno de huecos, y relanzarlo tampoco debe hacer que se meta a fechas nuevas
///      mas viejas solo por buscar huecos.
///   3) De ahi en adelante, revisa que "ayer" siempre quede guardado - por si la app se
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
                await RunGapFillBatchAsync(stoppingToken);
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

                // Se guarda SIEMPRE (con o sin datos) para marcar el día como revisado y que
                // no se vuelva a consultar al SP por él — "sin datos" también es una respuesta
                // válida que vale la pena recordar (fin de semana, día sin turno, etc.).
                await storage.UpsertDayAsync(cursor, rows, ct);
                consecutiveEmpty = hasData ? 0 : consecutiveEmpty + 1;
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

    /// <summary>
    /// Revisa, DENTRO del rango [OldestDateBackfilled, ayer], que dias quedaron sin marcar
    /// (tipicamente fines de semana que se saltaron con la logica vieja) y los marca. Nunca
    /// avanza mas atras de OldestDateBackfilled — eso es trabajo exclusivo de
    /// RunBackfillBatchAsync, y solo cuando tu decides reanudarlo a proposito.
    /// </summary>
    private async Task RunGapFillBatchAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IOeeHistoryStorageService>();
        var rawData = scope.ServiceProvider.GetRequiredService<IMetricsRawDataService>();

        var (oldestDate, _, _) = await storage.GetBackfillStateAsync(ct);
        if (oldestDate == null) return; // el backfill principal nunca ha corrido — nada que rellenar todavía

        var yesterday = DateTime.Today.AddDays(-1);
        var cursor = oldestDate.Value;
        int processed = 0;

        while (cursor <= yesterday && processed < DaysPerBatch && !ct.IsCancellationRequested)
        {
            if (!await storage.IsDayStoredAsync(cursor, ct))
            {
                var rows = await rawData.FetchDayRowsAsync(cursor, ct);
                await storage.UpsertDayAsync(cursor, rows, ct);
                processed++;
            }
            cursor = cursor.AddDays(1);
        }

        if (processed > 0)
        {
            _logger.LogInformation("Relleno de huecos de OEE historico: {N} días marcados entre {Oldest} y ayer.", processed, oldestDate.Value);
        }
    }

    private async Task EnsureYesterdayStoredAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IOeeHistoryStorageService>();
        var rawData = scope.ServiceProvider.GetRequiredService<IMetricsRawDataService>();

        var yesterday = DateTime.Today.AddDays(-1);
        if (await storage.IsDayStoredAsync(yesterday, ct)) return;

        var rows = await rawData.FetchDayRowsAsync(yesterday, ct);
        await storage.UpsertDayAsync(yesterday, rows, ct); // se guarda aunque no haya tenido producción
        _logger.LogInformation("Guardado automatico de ayer ({Date}) en PlantMetrics_OeeHistory.", yesterday);
    }
}
