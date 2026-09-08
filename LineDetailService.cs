using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

public interface ILineDetailService
{
    /// <summary>Snapshot de una sola línea, identificada por su Product_List_ID real, llamando al SP.</summary>
    Task<LineDetailSnapshot> GetSnapshotAsync(int lineId, CancellationToken ct = default);

    /// <summary>Construye el snapshot de una línea a partir de filas ya obtenidas (sin volver a golpear el SP).</summary>
    Task<LineDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int lineId, CancellationToken ct = default);
}

/// <summary>
/// Un solo servicio para las 53 líneas de la planta (sin contar Tube Mills, aunque también
/// funcionaría igual si algún día se necesitan sus líneas). Identifica la línea por su
/// Product_List_ID real — no hace falta tocar el SP, se resuelve con el mismo raw data que
/// ya trae MetricsBroadcastService en cada ciclo. Igual que el resto: HasPlan ahora respeta
/// Heijunka si hay datos esa semana (ver ProductLineMetric.CountsForStats para la misma idea).
/// </summary>
public class LineDetailService : ILineDetailService
{
    private readonly IMetricsRawDataService _rawDataService;
    private readonly IBreakScheduleService _breakSchedule;
    private readonly IHeijunkaService _heijunkaService;
    private readonly ILogger<LineDetailService> _logger;

    public LineDetailService(IMetricsRawDataService rawDataService, IBreakScheduleService breakSchedule, IHeijunkaService heijunkaService, ILogger<LineDetailService> logger)
    {
        _rawDataService = rawDataService;
        _breakSchedule = breakSchedule;
        _heijunkaService = heijunkaService;
        _logger = logger;
    }

    public async Task<LineDetailSnapshot> GetSnapshotAsync(int lineId, CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _rawDataService.FetchCurrentShiftRowsAsync(ct);
            return await BuildFromRowsAsync(rows, shiftDesc, lineId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo detalle de la línea {LineId}", lineId);
            return EmptySnapshot(lineId);
        }
    }

    public async Task<LineDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int lineId, CancellationToken ct = default)
    {
        var idLookup = rows.BuildProductListIdLookup();

        (int GroupId, string Desc)? found = null;
        foreach (var kv in idLookup)
        {
            if (kv.Value == lineId) { found = kv.Key; break; }
        }
        if (found == null) return EmptySnapshot(lineId);

        var (groupId, desc) = found.Value;

        var furnaceEntry = FurnaceCatalog.Map.FirstOrDefault(kv => kv.Value.ProductGroupIds.Contains(groupId));
        var furnaceId = furnaceEntry.Key;
        var furnaceName = furnaceEntry.Value.Name ?? string.Empty;

        // OJO: mismo caso que en los otros servicios — si el SP regresa esta línea 2 veces,
        // nos quedamos con la fila que trae los datos reales (mayor Total/Plan), no con la
        // primera que aparezca (podría ser la "vacía" del duplicado).
        var mainRow = rows
            .Where(r => r.ReportGroup == 1 && r.GroupId == groupId && r.Desc == desc)
            .OrderByDescending(r => r.Total)
            .ThenByDescending(r => r.PlannedForOee)
            .FirstOrDefault();

        var totalSap = rows
            .Where(r => r.ReportGroup == 3 && r.GroupId == groupId && r.Desc == desc)
            .Sum(r => r.TotalSap);

        var hourlyTrend = rows
            .Where(r => r.ReportGroup == 2 && r.GroupId == groupId && r.Desc == desc && !string.IsNullOrWhiteSpace(r.Hour) && r.Hour != "-")
            .GroupBy(r => r.Hour)
            .Select(g => new HourlyPoint { Hour = g.Key, Production = g.Sum(x => x.Total) })
            .ToList();
        hourlyTrend = ShiftTimeHelper.SortByShiftElapsed(hourlyTrend, shiftDesc);

        var shiftId = mainRow?.ShiftId
            ?? rows.FirstOrDefault(r => r.GroupId == groupId && r.Desc == desc)?.ShiftId
            ?? 0;

        // ---------- Tiempo de ciclo real aproximado por hora ----------
        // segundos disponibles (3600 - descansos que caen en esa hora) / piezas de esa hora.
        // Null si no hubo producción esa hora (no se puede estimar).
        //
        // OJO con la hora EN CURSO: si todavía no se completa, usar los segundos disponibles
        // de la HORA COMPLETA contra una producción que solo lleva una fracción de esa hora
        // infla el tiempo de ciclo real de forma artificial — por eso la última barra siempre
        // salía disparada. Se prorratean los segundos disponibles a los minutos que
        // REALMENTE han pasado de esa hora (con un piso del 5% para no dividir casi entre 0
        // en el primer minuto).
        var now = DateTime.Now;
        var cycleTimeTrend = hourlyTrend.Select(h =>
        {
            var availableSeconds = _breakSchedule.GetAvailableSeconds(shiftId, h.Hour);
            var isInProgress = false;

            if (TimeSpan.TryParse(h.Hour, out var bucketEnd))
            {
                if (bucketEnd == TimeSpan.Zero) bucketEnd = TimeSpan.FromHours(24);
                var bucketStart = bucketEnd - TimeSpan.FromHours(1);
                if (now.TimeOfDay >= bucketStart && now.TimeOfDay < bucketEnd)
                {
                    isInProgress = true;
                    var elapsedFraction = (now.TimeOfDay - bucketStart).TotalMinutes / 60.0;
                    availableSeconds = (int)Math.Round(availableSeconds * Math.Max(0.05, elapsedFraction));
                }
            }

            double? actual = h.Production > 0 ? (double)availableSeconds / h.Production : null;
            return new CycleTimePoint
            {
                Hour = h.Hour,
                Production = h.Production,
                ActualCycleTimeSecs = actual,
                AvailableSeconds = availableSeconds,
                IsInProgress = isInProgress
            };
        }).ToList();

        // Media del tiempo de ciclo real, solo con las horas del turno actual que tuvieron
        // producción — se muestra como línea de referencia en la gráfica (ya no hay banda
        // de desviación estándar, se quitó por no aportar).
        var samples = cycleTimeTrend.Where(p => p.ActualCycleTimeSecs.HasValue).Select(p => p.ActualCycleTimeSecs!.Value).ToList();
        double? actualMean = samples.Count > 0 ? samples.Average() : null;

        var plannedShift = mainRow?.PlannedForOee ?? 0;
        var shiftDurationHours = ShiftTimeHelper.GetDurationHours(shiftDesc);
        ShiftTimeHelper.ApplyExpectedCumulative(hourlyTrend, shiftDesc, shiftDurationHours, plannedShift);

        var heijunkaResult = await _heijunkaService.IsPlannedBatchAsync(new[] { lineId }, DateTime.Today, shiftId, ct);
        var heijunkaPlanned = heijunkaResult.TryGetValue(lineId, out var planned) ? planned : null;

        return new LineDetailSnapshot
        {
            ProductListId = lineId,
            ProductDesc = desc,
            FurnaceId = furnaceId,
            FurnaceName = furnaceName,
            ShiftDesc = shiftDesc,
            ShiftDurationHours = shiftDurationHours,
            CycleTimeSecs = mainRow?.CycleTimeSecs ?? 0,
            Total = mainRow?.Total ?? 0,
            AccumulatedRate = mainRow?.AccumRate ?? 0,
            PlannedShift = plannedShift,
            OeeShift = mainRow?.OeeShift ?? 0,
            TotalSap = totalSap,
            ExcludedFromSap = SapRules.IsExcluded(desc),
            HeijunkaPlanned = heijunkaPlanned,
            HourlyTrend = hourlyTrend,
            CycleTimeTrend = cycleTimeTrend,
            ActualCycleTimeMean = actualMean
        };
    }

    private static LineDetailSnapshot EmptySnapshot(int lineId) => new()
    {
        ProductListId = lineId,
        ProductDesc = "Línea no encontrada",
        ShiftDesc = string.Empty,
        HourlyTrend = new List<HourlyPoint>()
    };
}
