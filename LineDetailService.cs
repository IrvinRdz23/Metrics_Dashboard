using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

public interface ILineDetailService
{
    /// <summary>Snapshot de una sola línea, identificada por su Product_List_ID real, llamando al SP, EN VIVO.</summary>
    Task<LineDetailSnapshot> GetSnapshotAsync(int lineId, CancellationToken ct = default);

    /// <summary>Snapshot histórico de una línea para un día pasado + turno específico.</summary>
    Task<LineDetailSnapshot> GetHistoricalSnapshotAsync(int lineId, DateTime date, int shiftId, CancellationToken ct = default);

    /// <summary>Construye el snapshot de una línea a partir de filas ya obtenidas (sin volver a golpear el SP).</summary>
    Task<LineDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int lineId, CancellationToken ct = default, bool isLiveToday = true, DateTime? forDate = null);
}

/// <summary>
/// Un solo servicio para las 53 líneas de la planta (sin contar Tube Mills, aunque también
/// funcionaría igual si algún día se necesitan sus líneas). Identifica la línea por su
/// Product_List_ID real — no hace falta tocar el SP, se resuelve con el mismo raw data que
/// ya trae MetricsBroadcastService en cada ciclo. Igual que el resto: HasPlan ahora respeta
/// Heijunka si hay datos esa semana (ver ProductLineMetric.CountsForStats para la misma idea).
///
/// Ahora soporta histórico: GetHistoricalSnapshotAsync usa FetchHistoricalRowsAsync en vez
/// de FetchCurrentShiftRowsAsync, y BuildFromRowsAsync recibe isLiveToday/forDate para que
/// Heijunka y el acumulado esperado usen la fecha correcta en vez de "hoy" a fuerzas.
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
            return await BuildFromRowsAsync(rows, shiftDesc, lineId, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo detalle de la línea {LineId}", lineId);
            return EmptySnapshot(lineId);
        }
    }

    public async Task<LineDetailSnapshot> GetHistoricalSnapshotAsync(int lineId, DateTime date, int shiftId, CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _rawDataService.FetchHistoricalRowsAsync(date, shiftId, ct);
            return await BuildFromRowsAsync(rows, shiftDesc, lineId, isLiveToday: false, forDate: date, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo histórico de la línea {LineId} de {Date} turno {ShiftId}", lineId, date, shiftId);
            return EmptySnapshot(lineId);
        }
    }

    public async Task<LineDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int lineId, CancellationToken ct = default, bool isLiveToday = true, DateTime? forDate = null)
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
        // Se usa el mismo Accumulated_Rate que ya calcula bien el SP y se le da la vuelta a
        // la fórmula: segundos_transcurridos = Accumulated_Rate × Ciclo_Teórico. Ese total se
        // reparte entre las horas: a cada hora ya completada le tocan sus 3600s completos, a
        // la hora en curso le toca lo que sobre, a las futuras nada.
        var shiftStart = ParseShiftStart(shiftDesc);
        var theoreticalCT = mainRow?.CycleTimeSecs ?? 0;
        var totalElapsedTheoreticalSeconds = shiftStart.HasValue && theoreticalCT > 0
            ? (mainRow?.AccumRate ?? 0) * theoreticalCT
            : 0;

        var cycleTimeTrend = hourlyTrend.Select(h =>
        {
            if (!shiftStart.HasValue || theoreticalCT <= 0 || !TimeSpan.TryParse(h.Hour, out var bucketEnd))
            {
                return new CycleTimePoint { Hour = h.Hour, Production = h.Production, ActualCycleTimeSecs = null };
            }
            if (bucketEnd == TimeSpan.Zero) bucketEnd = TimeSpan.FromHours(24); // "00:00" = fin del día
            var bucketStart = bucketEnd - TimeSpan.FromHours(1);

            var secondsBeforeThisHour = ElapsedSinceShiftStart(bucketStart, shiftStart.Value).TotalSeconds;
            var elapsedThisHour = Math.Clamp(totalElapsedTheoreticalSeconds - secondsBeforeThisHour, 0, 3600);
            var isInProgress = elapsedThisHour > 0 && elapsedThisHour < 3600;

            double? actual = (h.Production > 0 && elapsedThisHour > 0) ? elapsedThisHour / h.Production : null;

            return new CycleTimePoint
            {
                Hour = h.Hour,
                Production = h.Production,
                ActualCycleTimeSecs = actual,
                AvailableSeconds = (int)Math.Round(elapsedThisHour),
                IsInProgress = isInProgress && isLiveToday
            };
        }).ToList();

        var samples = cycleTimeTrend.Where(p => p.ActualCycleTimeSecs.HasValue).Select(p => p.ActualCycleTimeSecs!.Value).ToList();
        double? actualMean = samples.Count > 0 ? samples.Average() : null;

        var plannedShift = mainRow?.PlannedForOee ?? 0;
        var shiftDurationHours = ShiftTimeHelper.GetDurationHours(shiftDesc);
        ShiftTimeHelper.ApplyExpectedCumulative(hourlyTrend, shiftDesc, shiftDurationHours, plannedShift, isLiveToday);

        var effectiveDate = forDate ?? DateTime.Today;
        var heijunkaResult = await _heijunkaService.IsPlannedBatchAsync(new[] { lineId }, effectiveDate, shiftId, ct);
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

    private static readonly System.Text.RegularExpressions.Regex ShiftTimeRegex =
        new(@"(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static TimeSpan? ParseShiftStart(string? shiftDesc)
    {
        var m = ShiftTimeRegex.Match(shiftDesc ?? string.Empty);
        if (!m.Success) return null;
        return new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
    }

    private static TimeSpan ElapsedSinceShiftStart(TimeSpan time, TimeSpan shiftStart)
    {
        var elapsed = time - shiftStart;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = elapsed < TimeSpan.FromHours(-12) ? elapsed + TimeSpan.FromHours(24) : TimeSpan.Zero;
        }
        return elapsed;
    }

    private static LineDetailSnapshot EmptySnapshot(int lineId) => new()
    {
        ProductListId = lineId,
        ProductDesc = "Línea no encontrada",
        ShiftDesc = string.Empty,
        HourlyTrend = new List<HourlyPoint>()
    };
}
