namespace Metrics_Dashboard.Models;

// =====================================================================================
// CONFIGURACIÓN
// =====================================================================================

/// <summary>Sección "PlantMetrics" de appsettings.json.</summary>
public class PlantOptions
{
    /// <summary>Plant_List_ID que se manda al SP (Planta 4 = 3).</summary>
    public int PlantListId { get; set; } = 3;

    public string PlantName { get; set; } = "Planta 4";

    /// <summary>Cada cuántos segundos se empuja un snapshot nuevo por SignalR.</summary>
    public int RefreshSeconds { get; set; } = 20;

    /// <summary>Cuántos segundos se cachea la respuesta del SP del día en curso.</summary>
    public int LiveCacheSeconds { get; set; } = 5;

    /// <summary>
    /// Interruptor de Heijunka. false = Planta 4 todavía no lo usa: cuenta toda línea con
    /// Planned_Shift_for_OEE &gt; 0 (criterio de siempre). true = se consulta Heijunka_Plan_List
    /// y las reglas de "planeado / no planeado" aplican igual que en Planta 1.
    /// </summary>
    public bool HeijunkaEnabled { get; set; } = false;

    /// <summary>true = datos de mentira, para probar el dashboard sin SQL Server.</summary>
    public bool UseDemoData { get; set; } = false;

    /// <summary>Descansos (hora de inicio/fin "HH:mm"). Opcional: solo afinan el tiempo de ciclo por hora.</summary>
    public List<BreakWindow> Breaks { get; set; } = new();
}

public class BreakWindow
{
    public string Start { get; set; } = string.Empty;
    public string End { get; set; } = string.Empty;
}

// =====================================================================================
// CATÁLOGO DE GRUPOS (reemplaza a los "hornos" de Planta 1)
// =====================================================================================

/// <summary>
/// Product_Group_ID de Planta 4: 8 = Condenser, 9 = Battery Cooling Plate, 10 = Heater Core,
/// 11 = Residential. Es el ÚNICO lugar donde se mantiene este mapeo.
/// </summary>
public static class ProductGroupCatalog
{
    public static readonly IReadOnlyList<(int Id, string Name)> Groups = new List<(int, string)>
    {
        (8, "Condenser"),
        (9, "Battery Cooling Plate"),
        (10, "Heater Core"),
        (11, "Residential"),
    };

    public static bool IsValid(int groupId) => Groups.Any(g => g.Id == groupId);

    public static string NameOf(int groupId) =>
        Groups.FirstOrDefault(g => g.Id == groupId).Name ?? $"Grupo {groupId}";

    public static int IndexOf(int groupId)
    {
        for (int i = 0; i < Groups.Count; i++) if (Groups[i].Id == groupId) return i;
        return 0;
    }
}

// =====================================================================================
// FILAS CRUDAS DEL SP
// =====================================================================================

public record ShiftInfo(int ShiftId, string ShiftDesc);

/// <summary>
/// Una fila tal cual sale de [dbo].[Plant_Metrics_Production_Reports]. Report_Group 1 = acumulado
/// del turno, 2 = hora por hora, 3 = SAP. En Report_Group 1 el Product_List_ID siempre viene NULL
/// (aquí 0); en 2 y 3 sí viene.
/// </summary>
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

public static class RawRowsExtensions
{
    /// <summary>(Product_Group_ID, Product_Desc) -> Product_List_ID real, tomado de Report_Group 2/3.</summary>
    public static Dictionary<(int GroupId, string Desc), int> BuildProductListIdLookup(this IEnumerable<RawMetricRow> rows)
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

    public static List<ShiftInfo> DistinctShifts(this IEnumerable<RawMetricRow> rows) =>
        rows.Where(r => r.ShiftId > 0)
            .Select(r => new ShiftInfo(r.ShiftId, r.ShiftDesc))
            .Distinct()
            .OrderBy(s => s.ShiftId)
            .ToList();
}

// =====================================================================================
// MODELOS DE SALIDA (lo que viaja como JSON a las vistas)
// =====================================================================================

/// <summary>Una línea/celda (ej. "PTC BMW Condenser").</summary>
public class ProductLineMetric
{
    public string ProductDesc { get; set; } = string.Empty;
    public int GroupId { get; set; }
    public int ProductListId { get; set; }
    public int ProductOrder { get; set; }
    public double CycleTimeSecs { get; set; }

    /// <summary>Producido en tiempo real (Total).</summary>
    public int Total { get; set; }

    /// <summary>Lo que debería llevar a esta hora (Accumulated_Rate).</summary>
    public int AccumulatedRate { get; set; }

    /// <summary>Plan de fin de turno (Planned_Shift_for_OEE).</summary>
    public int PlannedShift { get; set; }

    /// <summary>OEE del turno tal cual lo calcula el SP (0..1+).</summary>
    public double OeeShift { get; set; }

    /// <summary>Acumulado SAP (Report_Group 3). En Planta 4 SAP aplica a TODAS las líneas.</summary>
    public int TotalSap { get; set; }

    /// <summary>true/false = lo que dice Heijunka; null = Heijunka apagado o sin datos esa semana.</summary>
    public bool? HeijunkaPlanned { get; set; }

    /// <summary>¿Cuenta para OEE/producción/plan? Heijunka manda si hay datos; si no, Planned_Shift_for_OEE &gt; 0.</summary>
    public bool CountsForStats => HeijunkaPlanned ?? (PlannedShift > 0);

    /// <summary>Heijunka dice "sin plan" pero produjo: no cuenta, se muestra en rojo solo en el detalle.</summary>
    public bool IsUnplannedProduction => HeijunkaPlanned == false && Total > 0;
}

public class HourlyPoint
{
    /// <summary>Etiqueta de cierre de hora ("07:00" = de 06 a 07).</summary>
    public string Hour { get; set; } = string.Empty;
    public int Production { get; set; }

    /// <summary>SAP de esa hora (aproximado: proporcional a la producción de cada línea).</summary>
    public int SapProduction { get; set; }

    /// <summary>Acumulado que se debería llevar al cierre de esa hora (o a la hora actual si va en curso).</summary>
    public int? ExpectedCumulative { get; set; }
}

/// <summary>Un grupo de producto (Condenser, Battery Cooling Plate, Heater Core, Residential).</summary>
public class GroupMetric
{
    public int GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public int ColorIndex { get; set; }

    /// <summary>TODAS las líneas del turno (también las sin plan), ordenadas por Product_Order.</summary>
    public List<ProductLineMetric> Lines { get; set; } = new();

    /// <summary>Hora por hora del grupo: solo líneas que cuentan, con SAP aproximado y acumulado esperado.</summary>
    public List<HourlyPoint> HourlyTrend { get; set; } = new();

    private List<ProductLineMetric> CountedLines => Lines.Where(l => l.CountsForStats).ToList();

    public int TotalProduction => CountedLines.Sum(l => l.Total);
    public int TotalPlanned => CountedLines.Sum(l => l.PlannedShift);
    public int TotalSap => CountedLines.Sum(l => l.TotalSap);
    public int RemainingToPlan => Math.Max(0, TotalPlanned - TotalProduction);

    /// <summary>Promedio del OEE de las líneas que cuentan.</summary>
    public double Oee => CountedLines.Count == 0 ? 0 : CountedLines.Average(l => l.OeeShift);

    /// <summary>SAP / producción de las líneas que cuentan (todas aplican a SAP).</summary>
    public double SapPercent
    {
        get
        {
            var prod = CountedLines.Sum(l => l.Total);
            return prod <= 0 ? 0 : (double)CountedLines.Sum(l => l.TotalSap) / prod;
        }
    }

    public int CountedLineCount => CountedLines.Count;
    public int LinesWithProduction => CountedLines.Count(l => l.Total > 0);

    public ProductLineMetric? WorstLine => CountedLines.OrderBy(l => l.OeeShift).FirstOrDefault();
    public ProductLineMetric? BestLine => CountedLines.Where(l => l.Total > 0).OrderByDescending(l => l.OeeShift).FirstOrDefault();
}

/// <summary>Payload del dashboard general (también viaja por SignalR).</summary>
public class PlantDashboardSnapshot
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string PlantName { get; set; } = "Planta 4";

    /// <summary>Tal cual el Shift_Desc del SP — nunca hardcodeado.</summary>
    public string ShiftDesc { get; set; } = string.Empty;
    public int ShiftId { get; set; }
    public double ShiftDurationHours { get; set; } = 8.0;

    /// <summary>Fecha del reporte (la del inicio del turno).</summary>
    public DateTime EventDate { get; set; } = DateTime.Today;

    public bool HeijunkaEnabled { get; set; }

    /// <summary>Los 4 grupos, en el orden del catálogo (2x2).</summary>
    public List<GroupMetric> Groups { get; set; } = new();

    public bool IsHistorical { get; set; }
    public DateTime? HistoricalDate { get; set; }
    public int? HistoricalShiftId { get; set; }

    public int TotalProduction => Groups.Sum(g => g.TotalProduction);
    public int TotalPlanned => Groups.Sum(g => g.TotalPlanned);

    /// <summary>Promedio del OEE de todas las líneas que cuentan de la planta.</summary>
    public double PlantOee
    {
        get
        {
            var all = Groups.SelectMany(g => g.Lines).Where(l => l.CountsForStats).ToList();
            return all.Count == 0 ? 0 : all.Average(l => l.OeeShift);
        }
    }
}

/// <summary>Detalle de un grupo (vista /Group/{id}).</summary>
public class GroupDetailSnapshot
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string PlantName { get; set; } = "Planta 4";
    public string ShiftDesc { get; set; } = string.Empty;
    public int ShiftId { get; set; }
    public double ShiftDurationHours { get; set; } = 8.0;
    public DateTime EventDate { get; set; } = DateTime.Today;
    public bool HeijunkaEnabled { get; set; }
    public GroupMetric Group { get; set; } = new();

    public bool IsHistorical { get; set; }
    public DateTime? HistoricalDate { get; set; }
    public int? HistoricalShiftId { get; set; }
}

/// <summary>
/// Tiempo de ciclo real aproximado de una hora: segundos realmente disponibles para producir
/// en esa hora (sin descansos, ajustados a lo que el SP espera acumulado) ÷ piezas producidas.
/// </summary>
public class CycleTimePoint
{
    public string Hour { get; set; } = string.Empty;
    public int Production { get; set; }
    public double AvailableSeconds { get; set; }
    public double? ActualCycleTimeSecs { get; set; }

    /// <summary>true = la hora todavía no termina (el dato aún se está formando).</summary>
    public bool IsInProgress { get; set; }
}

/// <summary>TV individual de una línea (vista /Line/{id}).</summary>
public class LineDetailSnapshot
{
    public int ProductListId { get; set; }
    public string ProductDesc { get; set; } = string.Empty;
    public int GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public int ColorIndex { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string PlantName { get; set; } = "Planta 4";
    public string ShiftDesc { get; set; } = string.Empty;
    public int ShiftId { get; set; }
    public double ShiftDurationHours { get; set; } = 8.0;
    public DateTime EventDate { get; set; } = DateTime.Today;

    public double CycleTimeSecs { get; set; }
    public int Total { get; set; }
    public int AccumulatedRate { get; set; }
    public int PlannedShift { get; set; }
    public double OeeShift { get; set; }
    public int TotalSap { get; set; }
    public bool? HeijunkaPlanned { get; set; }

    public bool HasPlan => HeijunkaPlanned ?? (PlannedShift > 0);
    public bool IsUnplannedProduction => HeijunkaPlanned == false && Total > 0;
    public int RemainingToPlan => Math.Max(0, PlannedShift - Total);
    public double SapPercent => Total <= 0 ? 0 : (double)TotalSap / Total;

    public List<HourlyPoint> HourlyTrend { get; set; } = new();
    public List<CycleTimePoint> CycleTimeTrend { get; set; } = new();

    /// <summary>Tiempo de ciclo real promedio del turno (segundos disponibles ÷ piezas). Null sin producción.</summary>
    public double? ActualCycleTimeMean { get; set; }

    public bool IsHistorical { get; set; }
    public DateTime? HistoricalDate { get; set; }
    public int? HistoricalShiftId { get; set; }
}
