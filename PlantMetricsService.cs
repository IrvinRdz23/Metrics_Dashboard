using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

public interface IPlantMetricsService
{
    Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>Snapshot histórico de un día pasado + un turno específico (1, 2 o 3).</summary>
    Task<PlantDashboardSnapshot> GetHistoricalSnapshotAsync(DateTime date, int shiftId, CancellationToken ct = default);

    /// <summary>Construye el snapshot general a partir de filas ya obtenidas (sin volver a golpear el SP).</summary>
    PlantDashboardSnapshot BuildFromRows(List<RawMetricRow> rows, string shiftDesc);
}

/// <summary>
/// Dashboard GENERAL. Furnaces incluye 1-5 (los que sí se muestran como card fija) y también
/// Tube Mills al final (FurnaceId=6, solo para el 6to recuadro que alterna con la tendencia
/// cada 15s) — pero TotalProduction/TotalPlanned/PlantOee siguen excluyendo Tube Mills (ver
/// esas propiedades en PlantDashboardSnapshot). Ya no ejecuta el SP directamente: usa
/// IMetricsRawDataService, que se comparte con IFurnaceDetailService para que el SP se
/// llame una sola vez por ciclo.
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

    public async Task<PlantDashboardSnapshot> GetHistoricalSnapshotAsync(DateTime date, int shiftId, CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _rawDataService.FetchHistoricalRowsAsync(date, shiftId, ct);
            var snapshot = BuildFromRows(rows, shiftDesc);
            snapshot.IsHistorical = true;
            snapshot.HistoricalDate = date.Date;
            snapshot.HistoricalShiftId = shiftId;
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo histórico de {Date} turno {ShiftId}", date, shiftId);
            var empty = EmptySnapshot();
            empty.IsHistorical = true;
            empty.HistoricalDate = date.Date;
            empty.HistoricalShiftId = shiftId;
            return empty;
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

        // ---------- Carrusel de la barra superior: Core Builders / End of Line / Tube Mills ----------
        // Core Builders y End of Line se dividen con la MISMA regla que ya usamos para el
        // % de SAP (CB / Clam Shell / CM1 = Core Builders, el resto = End of Line), pero
        // aquí sobre Furnace 1-5. Tube Mills es aparte, sin distinción, aunque nunca
        // aparece como card en el grid del dashboard general.
        var furnaceCountedRows = rows.Where(r => r.ReportGroup == 1 && r.PlannedForOee != 0 && groupToFurnace.ContainsKey(r.GroupId)).ToList();
        var coreBuilderRows = furnaceCountedRows.Where(r => SapRules.IsExcluded(r.Desc)).ToList();
        var endOfLineRows = furnaceCountedRows.Where(r => !SapRules.IsExcluded(r.Desc)).ToList();
        var tubeMillsRows = rows.Where(r => r.ReportGroup == 1 && r.PlannedForOee != 0 && r.GroupId == 7).ToList();

        static KpiGroup BuildKpiGroup(string label, List<RawMetricRow> countedRows) => new()
        {
            Label = label,
            TotalProduction = countedRows.Sum(r => r.Total),
            TotalPlanned = countedRows.Sum(r => r.PlannedForOee),
            Oee = countedRows.Count == 0 ? 0 : countedRows.Average(r => r.OeeShift)
        };

        var topKpiGroups = new List<KpiGroup>
        {
            BuildKpiGroup("Core Builders", coreBuilderRows),
            BuildKpiGroup("End of Line", endOfLineRows),
            BuildKpiGroup("Tube Mills", tubeMillsRows)
        };

        // ---------- Tube Mills como Furnace #6 (para el 6to recuadro del grid, que alterna con
        // la tendencia cada 15s, y para el modal si le dan clic) — nunca cuenta en TotalProduction/
        // TotalPlanned/PlantOee (ver esas propiedades en el modelo, ya filtran FurnaceId<=5). ----------
        var tubeMills = new FurnaceMetric { FurnaceId = 6, FurnaceName = "Tube Mills" };
        var tubeMillsLinesIndex = new Dictionary<string, ProductLineMetric>();
        foreach (var r in rows.Where(r => r.ReportGroup == 1 && r.GroupId == 7))
        {
            var line = new ProductLineMetric
            {
                ProductDesc = r.Desc,
                ProductListId = productListIdLookup.TryGetValue((r.GroupId, r.Desc), out var tmPlid) ? tmPlid : 0,
                ProductOrder = r.ProductOrder,
                CycleTimeSecs = r.CycleTimeSecs,
                Total = r.Total,
                AccumulatedRate = r.AccumRate,
                PlannedShift = r.PlannedForOee,
                OeeShift = r.OeeShift,
                ExcludedFromSap = SapRules.IsExcluded(r.Desc),
            };
            tubeMills.Lines.Add(line);
            tubeMillsLinesIndex[r.Desc] = line;
        }
        foreach (var r in rows.Where(r => r.ReportGroup == 3 && r.GroupId == 7 && r.TotalSap > 0))
        {
            if (tubeMillsLinesIndex.TryGetValue(r.Desc, out var line)) line.TotalSap += r.TotalSap;
        }
        tubeMills.Lines = tubeMills.Lines.OrderBy(l => l.ProductOrder).ToList();
        furnaces.Add(tubeMills);

        return new PlantDashboardSnapshot
        {
            ShiftDesc = shiftDesc,
            ShiftDurationHours = ShiftTimeHelper.GetDurationHours(shiftDesc),
            Furnaces = furnaces,
            HourlyTrend = hourlyTotals.Select(kv => new HourlyPoint { Hour = kv.Key, Production = kv.Value }).ToList(),
            TopKpiGroups = topKpiGroups
        };
    }

    private static PlantDashboardSnapshot EmptySnapshot() => new()
    {
        ShiftDesc = string.Empty,
        Furnaces = Enumerable.Range(1, 5)
            .Select(id => new FurnaceMetric { FurnaceId = id, FurnaceName = $"Furnace {id}" })
            .Append(new FurnaceMetric { FurnaceId = 6, FurnaceName = "Tube Mills" })
            .ToList(),
        HourlyTrend = new List<HourlyPoint>(),
        TopKpiGroups = new List<KpiGroup>
        {
            new() { Label = "Core Builders" },
            new() { Label = "End of Line" },
            new() { Label = "Tube Mills" }
        }
    };
}
