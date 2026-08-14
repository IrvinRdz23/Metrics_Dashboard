using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

public interface IFurnaceDetailService
{
    /// <summary>Snapshot completo de un horno (o Tube Mills con furnaceId=6), llamando al SP.</summary>
    Task<FurnaceDetailSnapshot> GetSnapshotAsync(int furnaceId, CancellationToken ct = default);

    /// <summary>Construye el snapshot de un horno a partir de filas ya obtenidas (sin volver a golpear el SP).</summary>
    Task<FurnaceDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int furnaceId, CancellationToken ct = default);
}

/// <summary>
/// Un solo servicio para los 6 dashboards de detalle (Furnace 1..5 + Tube Mills), todos
/// resueltos por FurnaceCatalog. A diferencia del dashboard general, aquí SÍ se incluyen
/// todas las líneas (no solo un top 5) y SÍ se contempla Product_Group_ID=7 (Tube Mills)
/// cuando furnaceId=6.
///
/// Igual que PlantMetricsService: qué línea "cuenta" ya no es solo Planned_Shift_for_OEE != 0,
/// sino lo que diga Heijunka para ese día/turno (con respaldo al criterio viejo si Heijunka
/// no tiene datos esa semana). Ver ProductLineMetric.CountsForStats.
/// </summary>
public class FurnaceDetailService : IFurnaceDetailService
{
    private readonly IMetricsRawDataService _rawDataService;
    private readonly IHeijunkaService _heijunkaService;
    private readonly ILogger<FurnaceDetailService> _logger;

    public FurnaceDetailService(IMetricsRawDataService rawDataService, IHeijunkaService heijunkaService, ILogger<FurnaceDetailService> logger)
    {
        _rawDataService = rawDataService;
        _heijunkaService = heijunkaService;
        _logger = logger;
    }

    public async Task<FurnaceDetailSnapshot> GetSnapshotAsync(int furnaceId, CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _rawDataService.FetchCurrentShiftRowsAsync(ct);
            return await BuildFromRowsAsync(rows, shiftDesc, furnaceId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo detalle del horno {FurnaceId}", furnaceId);
            return EmptySnapshot(furnaceId);
        }
    }

    public async Task<FurnaceDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int furnaceId, CancellationToken ct = default)
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
            ShiftDesc = shiftDesc,
            ShiftDurationHours = ShiftTimeHelper.GetDurationHours(shiftDesc)
        };

        var linesIndex = new Dictionary<string, ProductLineMetric>();
        var productListIdLookup = rows.BuildProductListIdLookup();

        // Se incluyen TODAS las líneas (incluso con Planned_Shift_for_OEE = 0) para poder
        // mostrarlas en el detalle; FurnaceDetailSnapshot ya se encarga de no contarlas en
        // las estadísticas (ver CountedLines en el modelo).
        //
        // OJO: el SP a veces regresa la MISMA línea 2 veces (probablemente por su join interno
        // a Heijunka_Plan_List) — si ya la vimos, no se agrega una segunda entrada, se
        // actualiza la existente con los datos más "reales" de las dos filas.
        foreach (var r in rows.Where(r => r.ReportGroup == 1 && groupIds.Contains(r.GroupId)))
        {
            if (linesIndex.TryGetValue(r.Desc, out var existing))
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

        var hourlyTotals = new Dictionary<string, int>();
        foreach (var r in rows.Where(r => r.ReportGroup == 2 && groupIds.Contains(r.GroupId) && !string.IsNullOrWhiteSpace(r.Hour) && r.Hour != "-"))
        {
            hourlyTotals.TryGetValue(r.Hour, out var acc);
            hourlyTotals[r.Hour] = acc + r.Total;
        }

        snapshot.Lines = snapshot.Lines.OrderBy(l => l.ProductOrder).ToList();

        // ---------- HEIJUNKA: siempre en vivo (no hay vista histórica de horno todavía) ----------
        var today = DateTime.Today;
        var shiftId = rows.FirstOrDefault(r => r.ReportGroup == 1)?.ShiftId ?? 0;
        var heijunkaResults = await _heijunkaService.IsPlannedBatchAsync(
            snapshot.Lines.Select(l => l.ProductListId).Where(id => id > 0), today, shiftId, ct);
        foreach (var line in snapshot.Lines)
        {
            line.HeijunkaPlanned = heijunkaResults.TryGetValue(line.ProductListId, out var planned) ? planned : null;
        }

        snapshot.HourlyTrend = ShiftTimeHelper.SortByShiftElapsed(
            hourlyTotals.Select(kv => new HourlyPoint { Hour = kv.Key, Production = kv.Value }).ToList(), shiftDesc);
        ShiftTimeHelper.ApplyExpectedCumulative(snapshot.HourlyTrend, shiftDesc, snapshot.ShiftDurationHours, snapshot.TotalPlanned);

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
