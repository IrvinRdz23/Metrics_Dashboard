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
public static class RawRowsExtensions
{
    public static Dictionary<(int GroupId, string Desc), int> BuildProductListIdLookup(this List<RawMetricRow> rows)
    {
        var lookup = new Dictionary<(int, string), int>();
        foreach (var r in rows)
        {
            if (r.ProductListId <= 0) continue;
            var key = (r.GroupId, r.Desc);
            if (!lookup.ContainsKey(key)) lookup[key] = r.ProductListId;
        }
        return lookup;
    }
}

public record RawMetricRow(
    int ReportGroup,
    int GroupId,
    string Desc,
    int ProductOrder,
    int ProductListId,
    double CycleTimeSecs,
    int PlannedForOee,
    int AccumRate,
    double OeeShift,
    int Total,
    int TotalSap,
    string Hour,
    int ShiftId,
    string ShiftDesc,
    string EventDateShort = "");

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
/// Tiempo de ciclo REAL aproximado de una hora: segundos disponibles para producir en esa
/// hora (3600 menos el traslape con descansos) ÷ piezas producidas. Null si no hubo
/// producción esa hora (no se puede estimar).
/// </summary>
public class CycleTimePoint
{
    public string Hour { get; set; } = string.Empty;
    public int Production { get; set; }
    public double? ActualCycleTimeSecs { get; set; }
}

/// <summary>
/// Snapshot de UNA sola línea/celda (ej. "PTC Clam Shell 1"), para su TV individual
/// montada arriba de esa línea en piso. Se identifica por Product_List_ID — el ID real
/// de tu base, tomado de las filas de Report_Group 2/3 (en la 1 siempre viene NULL).
/// </summary>
public class LineDetailSnapshot
{
    public int ProductListId { get; set; }
    public string ProductDesc { get; set; } = string.Empty;
    public int FurnaceId { get; set; }
    public string FurnaceName { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string ShiftDesc { get; set; } = string.Empty;
    public double ShiftDurationHours { get; set; } = 8.0;

    public double CycleTimeSecs { get; set; }
    public int Total { get; set; }
    public int AccumulatedRate { get; set; }
    public int PlannedShift { get; set; }
    public double OeeShift { get; set; }
    public int TotalSap { get; set; }
    public bool ExcludedFromSap { get; set; }

    /// <summary>true/false = lo que dice Heijunka para esta línea este día/turno; null = no hay
    /// datos de Heijunka esta semana (se usa el criterio viejo como respaldo).</summary>
    public bool? HeijunkaPlanned { get; set; }

    /// <summary>Si no hay plan para este turno, la línea existe pero no tiene nada que
    /// reportar — la vista debe mostrar el estado "sin producción". Heijunka manda si hay
    /// datos esta semana; si no, se cae al criterio viejo (Planned_Shift_for_OEE != 0).</summary>
    public bool HasPlan => HeijunkaPlanned ?? (PlannedShift > 0);

    /// <summary>Heijunka dice que esta línea NO tenía plan este turno, pero sí produjo algo.</summary>
    public bool IsUnplannedProduction => HeijunkaPlanned == false && Total > 0;

    public List<HourlyPoint> HourlyTrend { get; set; } = new();

    /// <summary>Tiempo de ciclo real aproximado por hora del turno actual (ver CycleTimePoint).</summary>
    public List<CycleTimePoint> CycleTimeTrend { get; set; } = new();

    /// <summary>Media y desviación estándar del tiempo de ciclo real, calculadas SOLO con las
    /// horas del turno actual que tuvieron producción. Null si hay menos de 2 muestras válidas
    /// (no alcanza para una desviación estándar con sentido).</summary>
    public double? ActualCycleTimeMean { get; set; }
    public double? ActualCycleTimeStdDev { get; set; }
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
    public double ShiftDurationHours { get; set; } = 8.0;

    /// <summary>TODAS las líneas del turno actual (incluidas las que no tienen plan),
    /// ordenadas por Product_Order. Las que no tienen plan no cuentan para nada — ver CountedLines.</summary>
    public List<ProductLineMetric> Lines { get; set; } = new();
    public List<HourlyPoint> HourlyTrend { get; set; } = new();

    /// <summary>Solo las líneas que SÍ cuentan (ver ProductLineMetric.CountsForStats).</summary>
    private List<ProductLineMetric> CountedLines => Lines.Where(l => l.CountsForStats).ToList();

    /// <summary>De las que cuentan, solo las que YA tienen al menos 1 pieza — únicas que
    /// entran al OEE ponderado (ver Oee).</summary>
    private List<ProductLineMetric> CountedLinesWithProduction => CountedLines.Where(l => l.Total > 0).ToList();

    /// <summary>Líneas con producción NO planeada según Heijunka — no cuentan para nada,
    /// se muestran en rojo aparte en el detalle.</summary>
    public List<ProductLineMetric> UnplannedLines => Lines.Where(l => l.IsUnplannedProduction).ToList();

    public int TotalProduction => CountedLines.Sum(l => l.Total);
    public int TotalPlanned => CountedLines.Sum(l => l.PlannedShift);
    public int TotalSap => CountedLines.Sum(l => l.TotalSap);
    public int RemainingToPlan => Math.Max(0, TotalPlanned - TotalProduction);

    /// <summary>OEE ponderado del horno — SOLO promedia líneas que ya tienen producción.
    /// Una línea planeada en 0 piezas no cuenta todavía; en cuanto hace 1 pieza, sí.</summary>
    public double Oee => CountedLinesWithProduction.Count == 0 ? 0 : CountedLinesWithProduction.Average(l => l.OeeShift);

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

    public ProductLineMetric? BestLine => CountedLinesWithProduction
        .OrderByDescending(l => l.OeeShift)
        .FirstOrDefault();

    public ProductLineMetric? WorstLine => CountedLinesWithProduction
        .OrderBy(l => l.OeeShift)
        .FirstOrDefault();

    // ---------- Separación por segmento (item 6): Core Builder / Back End (+SAP) / Clam
    // Shells (solo Furnace 1) / Tube Mills (solo su propia página, sin SAP). ----------
    private List<ProductLineMetric> CoreBuilderLines => CountedLines.Where(l => l.Segment == LineSegment.CoreBuilder).ToList();
    private List<ProductLineMetric> BackEndLines => CountedLines.Where(l => l.Segment == LineSegment.BackEnd).ToList();
    private List<ProductLineMetric> ClamShellLines => CountedLines.Where(l => l.Segment == LineSegment.ClamShell).ToList();
    private List<ProductLineMetric> TubeMillsLines => CountedLines.Where(l => l.Segment == LineSegment.TubeMills).ToList();

    private static double AvgOee(List<ProductLineMetric> lines)
    {
        var withProd = lines.Where(l => l.Total > 0).ToList();
        return withProd.Count == 0 ? 0 : withProd.Average(l => l.OeeShift);
    }

    public int CoreBuilderProduction => CoreBuilderLines.Sum(l => l.Total);
    public int CoreBuilderPlanned => CoreBuilderLines.Sum(l => l.PlannedShift);
    public int CoreBuilderRemaining => Math.Max(0, CoreBuilderPlanned - CoreBuilderProduction);
    public double CoreBuilderOee => AvgOee(CoreBuilderLines);

    public int BackEndProduction => BackEndLines.Sum(l => l.Total);
    public int BackEndPlanned => BackEndLines.Sum(l => l.PlannedShift);
    public int BackEndRemaining => Math.Max(0, BackEndPlanned - BackEndProduction);
    public double BackEndOee => AvgOee(BackEndLines);
    public double BackEndSapPercent
    {
        get
        {
            var eligible = BackEndLines.Where(l => !l.ExcludedFromSap).ToList();
            var prod = eligible.Sum(l => l.Total);
            var sap = eligible.Sum(l => l.TotalSap);
            return prod <= 0 ? 0 : (double)sap / prod;
        }
    }

    public int ClamShellProduction => ClamShellLines.Sum(l => l.Total);
    public int ClamShellPlanned => ClamShellLines.Sum(l => l.PlannedShift);
    public int ClamShellRemaining => Math.Max(0, ClamShellPlanned - ClamShellProduction);
    public double ClamShellOee => AvgOee(ClamShellLines);

    public int TubeMillsProduction => TubeMillsLines.Sum(l => l.Total);
    public int TubeMillsPlanned => TubeMillsLines.Sum(l => l.PlannedShift);
    public int TubeMillsRemaining => Math.Max(0, TubeMillsPlanned - TubeMillsProduction);
    public double TubeMillsOee => AvgOee(TubeMillsLines);
}
