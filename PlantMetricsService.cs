using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

public interface IPlantMetricsService
{
    Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>Construye el snapshot general a partir de filas ya obtenidas (sin volver a golpear el SP).</summary>
    PlantDashboardSnapshot BuildFromRows(List<RawMetricRow> rows, string shiftDesc);
}

/// <summary>
/// Dashboard GENERAL (los 5 hornos, sin Tube Mills) — Product_Group_ID 7 se excluye aquí.
/// Ya no ejecuta el SP directamente: usa IMetricsRawDataService, que se comparte con
/// IFurnaceDetailService para que el SP se llame una sola vez por ciclo.
/// </summary>
public class PlantMetricsService : IPlantMetricsService
{
    private readonly IMetricsRawDataService _rawDataService;
    private readonly ILogger<PlantMetricsService> _logger;

    public PlantMetricsService(IMetricsRawDataService rawDataService, ILogger<PlantMetricsService> logger)
    {
        _rawDataService = rawDataService;
        _logger = logger;
    }

    public async Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _rawDataService.FetchCurrentShiftRowsAsync(ct);
            return BuildFromRows(rows, shiftDesc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo snapshot general de Plant_Metrics_Production_Reports");
            return EmptySnapshot();
        }
    }

    public PlantDashboardSnapshot BuildFromRows(List<RawMetricRow> rows, string shiftDesc)
    {
        var furnaces = Enumerable.Range(1, 5)
            .Select(id => new FurnaceMetric { FurnaceId = id, FurnaceName = $"Furnace {id}" })
            .ToList();

        var linesIndex = new Dictionary<(int furnaceId, string desc), ProductLineMetric>();
        var productListIdLookup = rows.BuildProductListIdLookup();

        // Mapeo Product_Group_ID -> horno (1..5), tomado del catálogo compartido, sin Tube Mills (7).
        var groupToFurnace = FurnaceCatalog.Map
            .Where(kv => kv.Key >= 1 && kv.Key <= 5)
            .SelectMany(kv => kv.Value.ProductGroupIds.Select(gid => (GroupId: gid, FurnaceId: kv.Key)))
            .ToDictionary(x => x.GroupId, x => x.FurnaceId);

        // Se incluyen TODAS las líneas (incluso con Planned_Shift_for_OEE = 0) para poder
        // mostrarlas en el detalle; FurnaceMetric ya se encarga de no contarlas en las
        // estadísticas (ver CountedLines en el modelo).
        foreach (var r in rows.Where(r => r.ReportGroup == 1 && groupToFurnace.ContainsKey(r.GroupId)))
        {
            var furnaceId = groupToFurnace[r.GroupId];
            var line = new ProductLineMetric
            {
                ProductDesc = r.Desc,
                ProductListId = productListIdLookup.TryGetValue((r.GroupId, r.Desc), out var plid) ? plid : 0,
                ProductOrder = r.ProductOrder,
                CycleTimeSecs = r.CycleTimeSecs,
                Total = r.Total,
                AccumulatedRate = r.AccumRate,
                PlannedShift = r.PlannedForOee,
                OeeShift = r.OeeShift,
                ExcludedFromSap = SapRules.IsExcluded(r.Desc),
            };
            furnaces.First(f => f.FurnaceId == furnaceId).Lines.Add(line);
            linesIndex[(furnaceId, r.Desc)] = line;
        }

        foreach (var r in rows.Where(r => r.ReportGroup == 3 && r.TotalSap > 0 && groupToFurnace.ContainsKey(r.GroupId)))
        {
            var furnaceId = groupToFurnace[r.GroupId];
            if (linesIndex.TryGetValue((furnaceId, r.Desc), out var line))
            {
                line.TotalSap += r.TotalSap;
            }
        }

        var hourlyTotals = new SortedDictionary<string, int>();
        foreach (var r in rows.Where(r => r.ReportGroup == 2 && groupToFurnace.ContainsKey(r.GroupId) && !string.IsNullOrWhiteSpace(r.Hour) && r.Hour != "-"))
        {
            hourlyTotals.TryGetValue(r.Hour, out var acc);
            hourlyTotals[r.Hour] = acc + r.Total;
        }

        foreach (var f in furnaces)
        {
            f.Lines = f.Lines.OrderBy(l => l.ProductOrder).ToList();
        }

        return new PlantDashboardSnapshot
        {
            ShiftDesc = shiftDesc,
            Furnaces = furnaces,
            HourlyTrend = hourlyTotals.Select(kv => new HourlyPoint { Hour = kv.Key, Production = kv.Value }).ToList()
        };
    }

    private static PlantDashboardSnapshot EmptySnapshot() => new()
    {
        ShiftDesc = string.Empty,
        Furnaces = Enumerable.Range(1, 5)
            .Select(id => new FurnaceMetric { FurnaceId = id, FurnaceName = $"Furnace {id}" })
            .ToList(),
        HourlyTrend = new List<HourlyPoint>()
    };
}
