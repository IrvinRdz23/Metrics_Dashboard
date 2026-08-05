namespace Metrics_Dashboard.Models;

/// <summary>
/// Una fila cruda tal cual sale del SP [dbo].[Plant_Metrics_Production_Reports], ya filtrada
/// al turno actual (por hora real vs. el rango en Shift_Desc), pero SIN excluir ningún
/// Product_Group_ID todavía — eso lo decide cada consumidor (dashboard general excluye el 7
/// / Tube Mills; los dashboards de horno usan FurnaceCatalog para saber qué grupos les tocan).
///
/// Se calcula una sola vez por ciclo en IMetricsRawDataService y de ahí se derivan TODOS los
/// snapshots (el general y los 6 de detalle), para no golpear el SP más de una vez por poll.
/// </summary>
public record RawMetricRow(
    int ReportGroup,
    int GroupId,
    string Desc,
    int ProductOrder,
    double CycleTimeSecs,
    int PlannedForOee,
    int AccumRate,
    double OeeShift,
    int Total,
    int TotalSap,
    string Hour,
    int ShiftId,
    string ShiftDesc);

/// <summary>
/// Catálogo único de "hornos" para todo el proyecto — incluye Tube Mills como el horno #6.
/// Un solo lugar para mantener el mapeo Product_Group_ID -> horno; tanto el dashboard general
/// como los de detalle beben de aquí.
/// </summary>
public static class FurnaceCatalog
{
    public static readonly Dictionary<int, (string Name, int[] ProductGroupIds)> Map = new()
    {
        [1] = ("Furnace 1", new[] { 1, 6 }), // 6 = Clam Shells, comparten horno con 1
        [2] = ("Furnace 2", new[] { 2 }),
        [3] = ("Furnace 3", new[] { 3 }),
        [4] = ("Furnace 4", new[] { 4 }),
        [5] = ("Furnace 5", new[] { 5 }),
        [6] = ("Tube Mills", new[] { 7 }),
    };

    public static bool IsValidFurnaceId(int id) => Map.ContainsKey(id);
}

/// <summary>
/// Snapshot completo de UN horno (o Tube Mills), con TODAS sus líneas del turno actual
/// (no solo un top 5) — es lo que alimenta cada dashboard individual.
/// </summary>
public class FurnaceDetailSnapshot
{
    public int FurnaceId { get; set; }
    public string FurnaceName { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string ShiftDesc { get; set; } = string.Empty;

    /// <summary>TODAS las líneas del turno actual (incluidas las que no tienen plan),
    /// ordenadas por Product_Order. Las que no tienen plan no cuentan para nada — ver CountedLines.</summary>
    public List<ProductLineMetric> Lines { get; set; } = new();
    public List<HourlyPoint> HourlyTrend { get; set; } = new();

    /// <summary>Solo las líneas con plan > 0 — las únicas que cuentan para producción/plan/OEE.</summary>
    private List<ProductLineMetric> CountedLines => Lines.Where(l => l.PlannedShift > 0).ToList();

    public int TotalProduction => CountedLines.Sum(l => l.Total);
    public int TotalPlanned => CountedLines.Sum(l => l.PlannedShift);
    public int TotalSap => CountedLines.Sum(l => l.TotalSap);
    public int RemainingToPlan => Math.Max(0, TotalPlanned - TotalProduction);

    public double Oee => CountedLines.Count == 0 ? 0 : CountedLines.Average(l => l.OeeShift);
    public double SapPercent
    {
        get
        {
            var eligible = CountedLines.Where(l => !l.ExcludedFromSap).ToList();
            var prod = eligible.Sum(l => l.Total);
            var sap = eligible.Sum(l => l.TotalSap);
            return prod <= 0 ? 0 : (double)sap / prod;
        }
    }

    public int LinesWithProduction => Lines.Count(l => l.Total > 0);
    public int LinesWithoutProduction => Lines.Count(l => l.Total == 0);

    public ProductLineMetric? BestLine => CountedLines
        .Where(l => l.Total > 0)
        .OrderByDescending(l => l.OeeShift)
        .FirstOrDefault();

    public ProductLineMetric? WorstLine => CountedLines
        .Where(l => l.Total > 0)
        .OrderBy(l => l.OeeShift)
        .FirstOrDefault();
}
