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
/// Dashboard GENERAL. Furnaces incluye 1-5 y Tube Mills (FurnaceId=6) al final — Tube Mills
/// ahora es una card fija más del grid. TotalProduction/TotalPlanned/PlantOee siguen
/// excluyendo Tube Mills (ver esas propiedades en PlantDashboardSnapshot). Ya no ejecuta el
/// SP directamente: usa IMetricsRawDataService, compartido con IFurnaceDetailService para
/// que el SP se llame una sola vez por ciclo.
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

        var groupToFurnace = FurnaceCatalog.Map
            .Where(kv => kv.Key >= 1 && kv.Key <= 5)
            .SelectMany(kv => kv.Value.ProductGroupIds.Select(gid => (GroupId: gid, FurnaceId: kv.Key)))
            .ToDictionary(x => x.GroupId, x => x.FurnaceId);

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
                GroupId = r.GroupId,
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

        foreach (var f in furnaces)
        {
            f.Lines = f.Lines.OrderBy(l => l.ProductOrder).ToList();
        }

        // ---------- Tube Mills como Furnace #6 — ya es una card fija más del grid. ----------
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
                GroupId = r.GroupId,
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

        // ---------- HEIJUNKA ----------
        var effectiveDate = forDate ?? DateTime.Today;
        var shiftId = rows.FirstOrDefault(r => r.ReportGroup == 1)?.ShiftId ?? 0;
        var allLines = furnaces.SelectMany(f => f.Lines).ToList();
        var heijunkaResults = await _heijunkaService.IsPlannedBatchAsync(
            allLines.Select(l => l.ProductListId).Where(id => id > 0), effectiveDate, shiftId, ct);

        foreach (var line in allLines)
        {
            line.HeijunkaPlanned = heijunkaResults.TryGetValue(line.ProductListId, out var planned) ? planned : null;
        }

        // ---------- Carrusel de la barra superior: Core Builders / Back End / Clam Shells / Tube Mills ----------
        var furnace1to5Lines = furnaces.Where(f => f.FurnaceId <= 5).SelectMany(f => f.Lines).Where(l => l.CountsForStats).ToList();
        var coreBuilderLines = furnace1to5Lines.Where(l => l.Segment == LineSegment.CoreBuilder).ToList();
        var backEndLines = furnace1to5Lines.Where(l => l.Segment == LineSegment.BackEnd).ToList();
        var clamShellLines = furnace1to5Lines.Where(l => l.Segment == LineSegment.ClamShell).ToList();
        var tubeMillsLines = tubeMills.Lines.Where(l => l.CountsForStats).ToList();

        static KpiGroup BuildKpiGroup(string label, List<ProductLineMetric> countedLines)
        {
            var withProduction = countedLines.Where(l => l.Total > 0).ToList();
            return new KpiGroup
            {
                Label = label,
                TotalProduction = countedLines.Sum(l => l.Total),
                TotalPlanned = countedLines.Sum(l => l.PlannedShift),
                Oee = withProduction.Count == 0 ? 0 : withProduction.Average(l => l.OeeShift)
            };
        }

        var coreBuilderKpi = BuildKpiGroup("Core Builders", coreBuilderLines);
        var backEndKpi = BuildKpiGroup("Back End", backEndLines);
        var clamShellKpi = BuildKpiGroup("Clam Shells", clamShellLines);
        var tubeMillsKpi = BuildKpiGroup("Tube Mills", tubeMillsLines);

        var topKpiGroups = new List<KpiGroup> { coreBuilderKpi, backEndKpi, clamShellKpi, tubeMillsKpi };

        // ---------- Tendencia por hora — general (se deja calculada, ya no se muestra en el
        // grid) y las 4 por categoría, para la pantalla de tendencias (flecha del borde derecho). ----------
        var hourlyTotals = new Dictionary<string, int>();
        var hourlyBySegment = new Dictionary<string, Dictionary<string, int>>
        {
            [LineSegment.CoreBuilder] = new(),
            [LineSegment.BackEnd] = new(),
            [LineSegment.ClamShell] = new(),
            [LineSegment.TubeMills] = new()
        };

        foreach (var r in rows.Where(r => r.ReportGroup == 2 && !string.IsNullOrWhiteSpace(r.Hour) && r.Hour != "-"))
        {
            string? segment = null;
            if (r.GroupId == 7) segment = LineSegment.TubeMills;
            else if (groupToFurnace.ContainsKey(r.GroupId)) segment = LineSegment.Classify(r.GroupId, r.Desc);
            if (segment == null) continue;

            if (segment != LineSegment.TubeMills)
            {
                hourlyTotals.TryGetValue(r.Hour, out var plantAcc);
                hourlyTotals[r.Hour] = plantAcc + r.Total;
            }

            var dict = hourlyBySegment[segment];
            dict.TryGetValue(r.Hour, out var acc);
            dict[r.Hour] = acc + r.Total;
        }

        List<HourlyPoint> BuildSegmentTrend(string segment, int segmentTotalPlanned)
        {
            var trend = hourlyBySegment[segment].Select(kv => new HourlyPoint { Hour = kv.Key, Production = kv.Value }).ToList();
            trend = ShiftTimeHelper.SortByShiftElapsed(trend, shiftDesc);
            ShiftTimeHelper.ApplyExpectedCumulative(trend, shiftDesc, ShiftTimeHelper.GetDurationHours(shiftDesc), segmentTotalPlanned, isLiveToday);
            return trend;
        }

        var coreBuilderTrend = BuildSegmentTrend(LineSegment.CoreBuilder, coreBuilderKpi.TotalPlanned);
        var backEndTrend = BuildSegmentTrend(LineSegment.BackEnd, backEndKpi.TotalPlanned);
        var clamShellTrend = BuildSegmentTrend(LineSegment.ClamShell, clamShellKpi.TotalPlanned);
        var tubeMillsTrend = BuildSegmentTrend(LineSegment.TubeMills, tubeMillsKpi.TotalPlanned);

        // SAP escaneado por hora de Back End — aproximado (ver comentario en HourlyPoint.SapProduction):
        // se reparte el SAP total del día proporcional a cuánto se produjo cada hora.
        var backEndTotalSap = backEndLines.Sum(l => l.TotalSap);
        var backEndTotalProdForSap = backEndTrend.Sum(h => h.Production);
        if (backEndTotalProdForSap > 0)
        {
            foreach (var point in backEndTrend)
            {
                point.SapProduction = (int)Math.Round((double)point.Production / backEndTotalProdForSap * backEndTotalSap);
            }
        }

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
            CoreBuilderHourlyTrend = coreBuilderTrend,
            BackEndHourlyTrend = backEndTrend,
            ClamShellHourlyTrend = clamShellTrend,
            TubeMillsHourlyTrend = tubeMillsTrend,
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
        CoreBuilderHourlyTrend = new List<HourlyPoint>(),
        BackEndHourlyTrend = new List<HourlyPoint>(),
        ClamShellHourlyTrend = new List<HourlyPoint>(),
        TubeMillsHourlyTrend = new List<HourlyPoint>(),
        TopKpiGroups = new List<KpiGroup>
        {
            new() { Label = "Core Builders" },
            new() { Label = "Back End" },
            new() { Label = "Clam Shells" },
            new() { Label = "Tube Mills" }
        }
    };
}
