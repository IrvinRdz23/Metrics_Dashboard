using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

public interface ILineDetailService
{
    /// <summary>Snapshot de una sola línea, identificada por su Product_List_ID real, llamando al SP.</summary>
    Task<LineDetailSnapshot> GetSnapshotAsync(int lineId, CancellationToken ct = default);

    /// <summary>Construye el snapshot de una línea a partir de filas ya obtenidas (sin volver a golpear el SP).</summary>
    LineDetailSnapshot BuildFromRows(List<RawMetricRow> rows, string shiftDesc, int lineId);
}

/// <summary>
/// Un solo servicio para las 53 líneas de la planta (sin contar Tube Mills, aunque también
/// funcionaría igual si algún día se necesitan sus líneas). Identifica la línea por su
/// Product_List_ID real — no hace falta tocar el SP, se resuelve con el mismo raw data que
/// ya trae MetricsBroadcastService en cada ciclo.
/// </summary>
public class LineDetailService : ILineDetailService
{
    private readonly IMetricsRawDataService _rawDataService;
    private readonly IBreakScheduleService _breakSchedule;
    private readonly ILogger<LineDetailService> _logger;

    public LineDetailService(IMetricsRawDataService rawDataService, IBreakScheduleService breakSchedule, ILogger<LineDetailService> logger)
    {
        _rawDataService = rawDataService;
        _breakSchedule = breakSchedule;
        _logger = logger;
    }

    public async Task<LineDetailSnapshot> GetSnapshotAsync(int lineId, CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _rawDataService.FetchCurrentShiftRowsAsync(ct);
            return BuildFromRows(rows, shiftDesc, lineId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo detalle de la línea {LineId}", lineId);
            return EmptySnapshot(lineId);
        }
    }

    public LineDetailSnapshot BuildFromRows(List<RawMetricRow> rows, string shiftDesc, int lineId)
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

        var mainRow = rows.FirstOrDefault(r => r.ReportGroup == 1 && r.GroupId == groupId && r.Desc == desc);

        var totalSap = rows
            .Where(r => r.ReportGroup == 3 && r.GroupId == groupId && r.Desc == desc)
            .Sum(r => r.TotalSap);

        var hourlyTrend = rows
            .Where(r => r.ReportGroup == 2 && r.GroupId == groupId && r.Desc == desc && !string.IsNullOrWhiteSpace(r.Hour) && r.Hour != "-")
            .GroupBy(r => r.Hour)
            .Select(g => new HourlyPoint { Hour = g.Key, Production = g.Sum(x => x.Total) })
            .OrderBy(h => h.Hour)
            .ToList();

        // ---------- Tiempo de ciclo real aproximado por hora ----------
        // segundos disponibles (3600 - descansos que caen en esa hora) / piezas de esa hora.
        // Null si no hubo producción esa hora (no se puede estimar).
        var shiftId = mainRow?.ShiftId
            ?? rows.FirstOrDefault(r => r.GroupId == groupId && r.Desc == desc)?.ShiftId
            ?? 0;

        var cycleTimeTrend = hourlyTrend.Select(h =>
        {
            var availableSeconds = _breakSchedule.GetAvailableSeconds(shiftId, h.Hour);
            double? actual = h.Production > 0 ? (double)availableSeconds / h.Production : null;
            return new CycleTimePoint { Hour = h.Hour, Production = h.Production, ActualCycleTimeSecs = actual };
        }).ToList();

        // Media y desviación estándar (muestral, n-1) solo con las horas que sí tuvieron producción.
        // Se requieren al menos 2 muestras para que la desviación estándar tenga sentido.
        var samples = cycleTimeTrend.Where(p => p.ActualCycleTimeSecs.HasValue).Select(p => p.ActualCycleTimeSecs!.Value).ToList();
        double? actualMean = samples.Count > 0 ? samples.Average() : null;
        double? actualStdDev = samples.Count > 1
            ? Math.Sqrt(samples.Sum(v => Math.Pow(v - actualMean!.Value, 2)) / (samples.Count - 1))
            : null;

        return new LineDetailSnapshot
        {
            ProductListId = lineId,
            ProductDesc = desc,
            FurnaceId = furnaceId,
            FurnaceName = furnaceName,
            ShiftDesc = shiftDesc,
            CycleTimeSecs = mainRow?.CycleTimeSecs ?? 0,
            Total = mainRow?.Total ?? 0,
            AccumulatedRate = mainRow?.AccumRate ?? 0,
            PlannedShift = mainRow?.PlannedForOee ?? 0,
            OeeShift = mainRow?.OeeShift ?? 0,
            TotalSap = totalSap,
            ExcludedFromSap = SapRules.IsExcluded(desc),
            HourlyTrend = hourlyTrend,
            CycleTimeTrend = cycleTimeTrend,
            ActualCycleTimeMean = actualMean,
            ActualCycleTimeStdDev = actualStdDev
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
