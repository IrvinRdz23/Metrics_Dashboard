using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

public interface IFurnaceDetailService
{
    /// <summary>Snapshot completo de un horno (o Tube Mills con furnaceId=6), llamando al SP.</summary>
    Task<FurnaceDetailSnapshot> GetSnapshotAsync(int furnaceId, CancellationToken ct = default);

    /// <summary>Construye el snapshot de un horno a partir de filas ya obtenidas (sin volver a golpear el SP).</summary>
    FurnaceDetailSnapshot BuildFromRows(List<RawMetricRow> rows, string shiftDesc, int furnaceId);
}

/// <summary>
/// Un solo servicio para los 6 dashboards de detalle (Furnace 1..5 + Tube Mills), todos
/// resueltos por FurnaceCatalog. A diferencia del dashboard general, aquí SÍ se incluyen
/// todas las líneas (no solo un top 5) y SÍ se contempla Product_Group_ID=7 (Tube Mills)
/// cuando furnaceId=6.
/// </summary>
public class FurnaceDetailService : IFurnaceDetailService
{
    private readonly IMetricsRawDataService _rawDataService;
    private readonly ILogger<FurnaceDetailService> _logger;

    public FurnaceDetailService(IMetricsRawDataService rawDataService, ILogger<FurnaceDetailService> logger)
    {
        _rawDataService = rawDataService;
        _logger = logger;
    }

    public async Task<FurnaceDetailSnapshot> GetSnapshotAsync(int furnaceId, CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _rawDataService.FetchCurrentShiftRowsAsync(ct);
            return BuildFromRows(rows, shiftDesc, furnaceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo detalle del horno {FurnaceId}", furnaceId);
            return EmptySnapshot(furnaceId);
        }
    }

    public FurnaceDetailSnapshot BuildFromRows(List<RawMetricRow> rows, string shiftDesc, int furnaceId)
    {
        if (!FurnaceCatalog.Map.TryGetValue(furnaceId, out var info))
        {
            return EmptySnapshot(furnaceId);
        }

        var groupIds = info.ProductGroupIds.ToHashSet();
        var snapshot = new FurnaceDetailSnapshot
        {
            FurnaceId = furnaceId,
            FurnaceName = info.Name,
            ShiftDesc = shiftDesc
        };

        var linesIndex = new Dictionary<string, ProductLineMetric>();

        // Se incluyen TODAS las líneas (incluso con Planned_Shift_for_OEE = 0) para poder
        // mostrarlas en el detalle; FurnaceDetailSnapshot ya se encarga de no contarlas en
        // las estadísticas (ver CountedLines en el modelo).
        foreach (var r in rows.Where(r => r.ReportGroup == 1 && groupIds.Contains(r.GroupId)))
        {
            var line = new ProductLineMetric
            {
                ProductDesc = r.Desc,
                ProductOrder = r.ProductOrder,
                CycleTimeSecs = r.CycleTimeSecs,
                Total = r.Total,
                AccumulatedRate = r.AccumRate,
                PlannedShift = r.PlannedForOee,
                OeeShift = r.OeeShift,
                ExcludedFromSap = SapRules.IsExcluded(r.Desc),
            };
            snapshot.Lines.Add(line);
            linesIndex[r.Desc] = line;
        }

        foreach (var r in rows.Where(r => r.ReportGroup == 3 && r.TotalSap > 0 && groupIds.Contains(r.GroupId)))
        {
            if (linesIndex.TryGetValue(r.Desc, out var line))
            {
                line.TotalSap += r.TotalSap;
            }
        }

        var hourlyTotals = new SortedDictionary<string, int>();
        foreach (var r in rows.Where(r => r.ReportGroup == 2 && groupIds.Contains(r.GroupId) && !string.IsNullOrWhiteSpace(r.Hour) && r.Hour != "-"))
        {
            hourlyTotals.TryGetValue(r.Hour, out var acc);
            hourlyTotals[r.Hour] = acc + r.Total;
        }

        snapshot.Lines = snapshot.Lines.OrderBy(l => l.ProductOrder).ToList();
        snapshot.HourlyTrend = hourlyTotals.Select(kv => new HourlyPoint { Hour = kv.Key, Production = kv.Value }).ToList();

        return snapshot;
    }

    private static FurnaceDetailSnapshot EmptySnapshot(int furnaceId) => new()
    {
        FurnaceId = furnaceId,
        FurnaceName = FurnaceCatalog.Map.TryGetValue(furnaceId, out var info) ? info.Name : $"Furnace {furnaceId}",
        ShiftDesc = string.Empty,
        Lines = new List<ProductLineMetric>(),
        HourlyTrend = new List<HourlyPoint>()
    };
}
