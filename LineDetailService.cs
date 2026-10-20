using Metrics_Dashboard.Models;
using Microsoft.Extensions.Options;

namespace Metrics_Dashboard.Services;

public interface ILineDetailService
{
    Task<LineDetailSnapshot> GetSnapshotAsync(int lineId, CancellationToken ct = default);
    Task<LineDetailSnapshot> GetHistoricalSnapshotAsync(int lineId, DateTime date, int shiftId, CancellationToken ct = default);
    Task<LineDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int lineId, CancellationToken ct = default, bool isLive = true);
}

/// <summary>
/// TV individual de una línea (se identifica por su Product_List_ID real). Incluye el tiempo de
/// ciclo real por hora: los segundos que la línea realmente tuvo disponibles (lo que el SP espera
/// acumulado = Accumulated_Rate × tiempo de ciclo teórico, repartido entre las horas sin contar
/// descansos) entre las piezas producidas en esa hora.
/// </summary>
public class LineDetailService : ILineDetailService
{
    private readonly IMetricsRawDataService _raw;
    private readonly IHeijunkaService _heijunka;
    private readonly IBreakScheduleService _breaks;
    private readonly PlantOptions _options;
    private readonly ILogger<LineDetailService> _logger;

    public LineDetailService(IMetricsRawDataService raw, IHeijunkaService heijunka, IBreakScheduleService breaks,
        IOptions<PlantOptions> options, ILogger<LineDetailService> logger)
    {
        _raw = raw;
        _heijunka = heijunka;
        _breaks = breaks;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LineDetailSnapshot> GetSnapshotAsync(int lineId, CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _raw.FetchCurrentShiftRowsAsync(ct);
            return await BuildFromRowsAsync(rows, shiftDesc, lineId, ct, isLive: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo el detalle de la línea {LineId}", lineId);
            return EmptySnapshot(lineId);
        }
    }

    public async Task<LineDetailSnapshot> GetHistoricalSnapshotAsync(int lineId, DateTime date, int shiftId, CancellationToken ct = default)
    {
        try
        {
            var dayRows = await _raw.FetchDayRowsAsync(date, ct);
            var rows = dayRows.Where(r => r.ShiftId == shiftId).ToList();
            var shiftDesc = rows.Select(r => r.ShiftDesc).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty;

            var snap = await BuildFromRowsAsync(rows, shiftDesc, lineId, ct, isLive: false);
            snap.IsHistorical = true;
            snap.HistoricalDate = date.Date;
            snap.HistoricalShiftId = shiftId;
            return snap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo el histórico de la línea {LineId}", lineId);
            var empty = EmptySnapshot(lineId);
            empty.IsHistorical = true;
            empty.HistoricalDate = date.Date;
            empty.HistoricalShiftId = shiftId;
            return empty;
        }
    }

    public async Task<LineDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int lineId, CancellationToken ct = default, bool isLive = true)
    {
        var lookup = rows.BuildProductListIdLookup();
        var matches = lookup.Where(kv => kv.Value == lineId).Select(kv => kv.Key).ToList();
        if (matches.Count == 0) return EmptySnapshot(lineId);

        var (groupId, desc) = matches[0];
        var line = GroupBuilder.BuildLines(rows, groupId).FirstOrDefault(l => l.ProductDesc == desc);
        if (line == null) return EmptySnapshot(lineId);

        var now = DateTime.Now;
        var eventDate = GroupBuilder.EventDateOf(rows);
        var shiftId = rows.Select(r => r.ShiftId).FirstOrDefault(i => i > 0);

        var heijunka = await _heijunka.IsPlannedBatchAsync(new[] { lineId }, eventDate, shiftId, ct);
        GroupBuilder.ApplyHeijunka(new[] { line }, heijunka);

        var hourly = ShiftTimeHelper.FillTrend(GroupBuilder.LineHourly(rows, line), shiftDesc, isLive, now, line.PlannedShift);
        var (cycleTrend, mean) = BuildCycleTimes(hourly, line, shiftDesc, isLive, now);

        return new LineDetailSnapshot
        {
            ProductListId = lineId,
            ProductDesc = desc,
            GroupId = groupId,
            GroupName = ProductGroupCatalog.NameOf(groupId),
            ColorIndex = ProductGroupCatalog.IndexOf(groupId),
            PlantName = _options.PlantName,
            ShiftDesc = shiftDesc,
            ShiftId = shiftId,
            ShiftDurationHours = ShiftTimeHelper.GetDurationHours(shiftDesc),
            EventDate = eventDate,
            CycleTimeSecs = line.CycleTimeSecs,
            Total = line.Total,
            AccumulatedRate = line.AccumulatedRate,
            PlannedShift = line.PlannedShift,
            OeeShift = line.OeeShift,
            TotalSap = line.TotalSap,
            HeijunkaPlanned = line.HeijunkaPlanned,
            HourlyTrend = hourly,
            CycleTimeTrend = cycleTrend,
            ActualCycleTimeMean = mean
        };
    }

    /// <summary>
    /// Segundos totales disponibles = Accumulated_Rate × CT teórico. Se reparten entre las horas en
    /// proporción a su tiempo corrido sin descansos. CT real de la hora = segundos / piezas (null si
    /// no hubo producción — no se inventa un "0s").
    /// </summary>
    private (List<CycleTimePoint> Trend, double? Mean) BuildCycleTimes(List<HourlyPoint> hourly, ProductLineMetric line,
        string shiftDesc, bool isLive, DateTime now)
    {
        var buckets = ShiftTimeHelper.BuildBuckets(shiftDesc).ToDictionary(b => b.Label);
        var nowElapsed = isLive ? ShiftTimeHelper.ElapsedNowMinutes(shiftDesc, now) : double.MaxValue;

        // Segundos "crudos" (tiempo corrido menos descansos) por hora.
        var raw = new List<(HourlyPoint P, double Sec, bool InProgress)>();
        foreach (var p in hourly)
        {
            if (!buckets.TryGetValue(p.Hour, out var b)) { raw.Add((p, 0, false)); continue; }
            var end = Math.Min(b.EndMin, nowElapsed);
            var wall = Math.Max(0, end - b.StartMin);
            var avail = Math.Max(0, wall - _breaks.OverlapMinutes(shiftDesc, b.StartMin, end));
            raw.Add((p, avail * 60.0, isLive && nowElapsed < b.EndMin));
        }

        // Escalar para que la suma cuadre con lo que el SP espera acumulado.
        var target = line.AccumulatedRate * line.CycleTimeSecs;
        var rawSum = raw.Sum(r => r.Sec);
        var scale = target > 0 && rawSum > 0 ? target / rawSum : 1.0;

        var trend = raw.Select(r =>
        {
            var sec = r.Sec * scale;
            return new CycleTimePoint
            {
                Hour = r.P.Hour,
                Production = r.P.Production,
                AvailableSeconds = Math.Round(sec, 1),
                IsInProgress = r.InProgress,
                ActualCycleTimeSecs = r.P.Production > 0 && sec > 0 ? Math.Round(sec / r.P.Production, 1) : null
            };
        }).ToList();

        var withProd = trend.Where(t => t.Production > 0 && t.AvailableSeconds > 0).ToList();
        var totalProd = withProd.Sum(t => t.Production);
        double? mean = totalProd > 0 ? Math.Round(withProd.Sum(t => t.AvailableSeconds) / totalProd, 1) : null;

        return (trend, mean);
    }

    private LineDetailSnapshot EmptySnapshot(int lineId) => new()
    {
        ProductListId = lineId,
        ProductDesc = "Línea no encontrada",
        PlantName = _options.PlantName
    };
}
