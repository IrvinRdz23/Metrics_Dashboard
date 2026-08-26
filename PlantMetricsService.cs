using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

public interface IPlantMetricsService
{
    Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>Snapshot histórico de un día pasado + un turno específico (1, 2 o 3).</summary>
    Task<PlantDashboardSnapshot> GetHistoricalSnapshotAsync(DateTime date, int shiftId, CancellationToken ct = default);

    /// <summary>Construye el snapshot general a partir de filas ya obtenidas (sin volver a golpear el SP).</summary>
    Task<PlantDashboardSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, bool isLiveToday = true, DateTime? forDate = null, CancellationToken ct = default);
}

/// <summary>
/// Dashboard GENERAL. Furnaces incluye 1-5 (los que sí se muestran como card fija) y también
/// Tube Mills al final (FurnaceId=6, solo para el 6to recuadro que alterna con la tendencia
/// cada 15s) — pero TotalProduction/TotalPlanned/PlantOee siguen excluyendo Tube Mills (ver
/// esas propiedades en PlantDashboardSnapshot). Ya no ejecuta el SP directamente: usa
/// IMetricsRawDataService, que se comparte con IFurnaceDetailService para que el SP se
/// llame una sola vez por ciclo.
///
/// Desde que se agregó Heijunka: qué línea "cuenta" para OEE/producción/plan ya NO depende
/// solo de Planned_Shift_for_OEE != 0 — se le pregunta a IHeijunkaService (Heijunka_Plan_List)
/// si esa línea tenía plan ESTE día/turno específico. Si Heijunka no tiene datos para la
/// semana vigente, se cae automáticamente al criterio viejo (ver ProductLineMetric.CountsForStats).
/// </summary>
public class PlantMetricsService : IPlantMetricsService
{
    private readonly IMetricsRawDataService _rawDataService;
    private readonly IHeijunkaService _heijunkaService;
    private readonly ILogger<PlantMetricsService> _logger;

    public PlantMetricsService(IMetricsRawDataService rawDataService, IHeijunkaService heijunkaService, ILogger<PlantMetricsService> logger)
    {
        _rawDataService = rawDataService;
        _heijunkaService = heijunkaService;
        _logger = logger;
    }

    public async Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _rawDataService.FetchCurrentShiftRowsAsync(ct);
            return await BuildFromRowsAsync(rows, shiftDesc, ct: ct);
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
            var snapshot = await BuildFromRowsAsync(rows, shiftDesc, isLiveToday: false, forDate: date, ct: ct);
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

    public async Task<PlantDashboardSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, bool isLiveToday = true, DateTime? forDate = null, CancellationToken ct = default)
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
        //
        // OJO: el SP a veces regresa la MISMA línea 2 veces (probablemente por su join interno
        // a Heijunka_Plan_List, que ahora que esa tabla tiene datos reales para la semana
        // puede estar generando un fan-out). Si ya vimos esta línea (mismo horno + mismo
        // nombre), NO se agrega una segunda entrada — se actualiza la que ya existe con los
        // datos más "reales" (el mayor Total/Plan de las dos filas), para nunca duplicar.
        foreach (var r in rows.Where(r => r.ReportGroup == 1 && groupToFurnace.ContainsKey(r.GroupId)))
        {
            var furnaceId = groupToFurnace[r.GroupId];
            var key = (furnaceId, r.Desc);

            if (linesIndex.TryGetValue(key, out var existing))
            {
                if (r.Total > existing.Total || r.PlannedForOee > existing.PlannedShift)
                {
                    existing.Total = r.Total;
                    existing.AccumulatedRate = r.AccumRate;
                    existing.PlannedShift = r.PlannedForOee;
                    existing.OeeShift = r.OeeShift;
                    existing.CycleTimeSecs = r.CycleTimeSecs;
                }
                continue;
            }

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
            linesIndex[key] = line;
        }

        foreach (var r in rows.Where(r => r.ReportGroup == 3 && r.TotalSap > 0 && groupToFurnace.ContainsKey(r.GroupId)))
        {
            var furnaceId = groupToFurnace[r.GroupId];
            if (linesIndex.TryGetValue((furnaceId, r.Desc), out var line))
            {
                line.TotalSap += r.TotalSap;
            }
        }

        var hourlyTotals = new Dictionary<string, int>();
        foreach (var r in rows.Where(r => r.ReportGroup == 2 && groupToFurnace.ContainsKey(r.GroupId) && !string.IsNullOrWhiteSpace(r.Hour) && r.Hour != "-"))
        {
            hourlyTotals.TryGetValue(r.Hour, out var acc);
            hourlyTotals[r.Hour] = acc + r.Total;
        }

        foreach (var f in furnaces)
        {
            f.Lines = f.Lines.OrderBy(l => l.ProductOrder).ToList();
        }

        // ---------- Tube Mills como Furnace #6 (para el 6to recuadro del grid, que alterna con
        // la tendencia cada 15s, y para el modal si le dan clic) — nunca cuenta en TotalProduction/
        // TotalPlanned/PlantOee (ver esas propiedades en el modelo, ya filtran FurnaceId<=5). ----------
        var tubeMills = new FurnaceMetric { FurnaceId = 6, FurnaceName = "Tube Mills" };
        var tubeMillsLinesIndex = new Dictionary<string, ProductLineMetric>();
        foreach (var r in rows.Where(r => r.ReportGroup == 1 && r.GroupId == 7))
        {
            if (tubeMillsLinesIndex.TryGetValue(r.Desc, out var existingTm))
            {
                if (r.Total > existingTm.Total || r.PlannedForOee > existingTm.PlannedShift)
                {
                    existingTm.Total = r.Total;
                    existingTm.AccumulatedRate = r.AccumRate;
                    existingTm.PlannedShift = r.PlannedForOee;
                    existingTm.OeeShift = r.OeeShift;
                    existingTm.CycleTimeSecs = r.CycleTimeSecs;
                }
                continue;
            }

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

        // ---------- HEIJUNKA: se resuelve UNA vez para todas las líneas (Furnace 1-5 + Tube
        // Mills) con un solo batch, y se le pega el resultado a cada ProductLineMetric. Si
        // Heijunka no tiene datos para la semana vigente, todas quedan en null y cada línea
        // cae sola al criterio viejo (ver ProductLineMetric.CountsForStats). ----------
        var effectiveDate = forDate ?? DateTime.Today;
        var shiftId = rows.FirstOrDefault(r => r.ReportGroup == 1)?.ShiftId ?? 0;
        var allLines = furnaces.SelectMany(f => f.Lines).ToList();
        var heijunkaResults = await _heijunkaService.IsPlannedBatchAsync(
            allLines.Select(l => l.ProductListId).Where(id => id > 0), effectiveDate, shiftId, ct);

        foreach (var line in allLines)
        {
            line.HeijunkaPlanned = heijunkaResults.TryGetValue(line.ProductListId, out var planned) ? planned : null;
        }

        // ---------- Carrusel de la barra superior: Core Builders / End of Line / Tube Mills ----------
        // Core Builders y End of Line se dividen con la MISMA regla que ya usamos para el
        // % de SAP (CB / Clam Shell / CM1 = Core Builders, el resto = End of Line), pero
        // aquí sobre Furnace 1-5. Tube Mills es aparte, sin distinción, aunque nunca
        // aparece como card en el grid del dashboard general. Usa las líneas YA resueltas con
        // Heijunka (CountsForStats) en vez de filtrar el raw row por separado.
        var furnace1to5Lines = furnaces.Where(f => f.FurnaceId <= 5).SelectMany(f => f.Lines).Where(l => l.CountsForStats).ToList();
        var coreBuilderLines = furnace1to5Lines.Where(l => SapRules.IsExcluded(l.ProductDesc)).ToList();
        var endOfLineLines = furnace1to5Lines.Where(l => !SapRules.IsExcluded(l.ProductDesc)).ToList();
        var tubeMillsLines = tubeMills.Lines.Where(l => l.CountsForStats).ToList();

        static KpiGroup BuildKpiGroup(string label, List<ProductLineMetric> countedLines) => new()
        {
            Label = label,
            TotalProduction = countedLines.Sum(l => l.Total),
            TotalPlanned = countedLines.Sum(l => l.PlannedShift),
            Oee = countedLines.Count == 0 ? 0 : countedLines.Average(l => l.OeeShift)
        };

        var topKpiGroups = new List<KpiGroup>
        {
            BuildKpiGroup("Core Builders", coreBuilderLines),
            BuildKpiGroup("Back End", endOfLineLines),
            BuildKpiGroup("Tube Mills", tubeMillsLines)
        };

        var hourlyTrend = hourlyTotals.Select(kv => new HourlyPoint { Hour = kv.Key, Production = kv.Value }).ToList();
        hourlyTrend = ShiftTimeHelper.SortByShiftElapsed(hourlyTrend, shiftDesc);
        var plantTotalPlanned = furnaces.Where(f => f.FurnaceId <= 5).Sum(f => f.TotalPlanned);
        ShiftTimeHelper.ApplyExpectedCumulative(hourlyTrend, shiftDesc, ShiftTimeHelper.GetDurationHours(shiftDesc), plantTotalPlanned, isLiveToday);

        return new PlantDashboardSnapshot
        {
            ShiftDesc = shiftDesc,
            ShiftDurationHours = ShiftTimeHelper.GetDurationHours(shiftDesc),
            Furnaces = furnaces,
            HourlyTrend = hourlyTrend,
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
            new() { Label = "Back End" },
            new() { Label = "Tube Mills" }
        }
    };
}
