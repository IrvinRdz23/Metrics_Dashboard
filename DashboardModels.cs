namespace Metrics_Dashboard.Models;

/// <summary>
/// Clasifica cada línea en uno de 4 "segmentos" para poder separar sus métricas:
///   - TubeMills: Product_Group_ID = 7.
///   - ClamShell: Product_Group_ID = 6 (Furnace 1 Clam Shells) — SIEMPRE aparte de Core
///     Builder, aunque su nombre también contenga "CB" o similar.
///   - CoreBuilder: el resto de líneas cuyo nombre matchea SapRules.IsExcluded (CB/CM1).
///   - BackEnd: todo lo demás (Furnace 1-5, sin Clam Shells) — el único segmento con SAP %.
/// </summary>
public static class LineSegment
{
    public const string CoreBuilder = "coreBuilder";
    public const string BackEnd = "backEnd";
    public const string ClamShell = "clamShell";
    public const string TubeMills = "tubeMills";

    public static string Classify(int groupId, string desc)
    {
        if (groupId == 7) return TubeMills;
        if (groupId == 6) return ClamShell;
        return SapRules.IsExcluded(desc) ? CoreBuilder : BackEnd;
    }
}

/// <summary>
/// Representa una línea/producto individual dentro de un horno (ej. "PTC Clam Shell 1").
/// Viene de Report_Group=1 (día/turno acumulado, turno actual, con plan > 0) + Report_Group=3 (SAP).
/// </summary>
public class ProductLineMetric
{
    public string ProductDesc { get; set; } = string.Empty;
    public double CycleTimeSecs { get; set; }

    /// <summary>Product_Group_ID crudo del SP (1-5 = Furnace 1-5, 6 = Furnace 1 Clam Shells,
    /// 7 = Tube Mills) — de aquí sale Segment.</summary>
    public int GroupId { get; set; }

    /// <summary>Segmento para separar métricas: coreBuilder / backEnd / clamShell / tubeMills.
    /// Ver LineSegment.Classify.</summary>
    public string Segment => LineSegment.Classify(GroupId, ProductDesc);

    /// <summary>Product_List_ID real de tu base — viene de Report_Group 2/3 (en la 1 es NULL,
    /// así que se empareja por Product_Group_ID+Desc). Es el ID que usa /Line/{id}.</summary>
    public int ProductListId { get; set; }

    /// <summary>Orden de línea tal cual lo define Product_Order en el SP — usado para ordenar el detalle y el carrusel.</summary>
    public int ProductOrder { get; set; }

    /// <summary>Total producido en tiempo real (columna Total, Report_Group=1).</summary>
    public int Total { get; set; }

    /// <summary>Lo que debería llevar producido a esta hora (columna Accumulated_Rate).</summary>
    public int AccumulatedRate { get; set; }

    /// <summary>Plan completo de fin de turno (columna Planned_Shift_for_OEE).</summary>
    public int PlannedShift { get; set; }

    /// <summary>OEE de turno tal cual lo calcula el SP (Total / Accumulated_Rate). Ya viene como 0..1+.</summary>
    public double OeeShift { get; set; }

    /// <summary>Acumulado SAP (Report_Group=3, columna Total_SAP).</summary>
    public int TotalSap { get; set; }

    /// <summary>
    /// Líneas cuyo nombre incluye "CB", "Clam Shell" o "CM1" no participan en el % de SAP
    /// (ni como numerador ni como denominador) — se marcan aquí para que la UI las muestre
    /// con "-" en vez de un número/porcentaje. Ver SapRules.IsExcluded.
    /// </summary>
    public bool ExcludedFromSap { get; set; }

    /// <summary>
    /// true = Heijunka dice que esta línea SÍ tenía plan este día/turno.
    /// false = Heijunka dice que NO tenía plan.
    /// null = no hay datos de Heijunka para esta semana (o la línea no se pudo mapear) —
    /// en ese caso se usa el criterio viejo como respaldo (ver CountsForStats).
    /// </summary>
    public bool? HeijunkaPlanned { get; set; }

    /// <summary>
    /// ¿Esta línea cuenta para OEE/producción/plan? Heijunka manda si hay datos esta semana;
    /// si no los hay, se cae al criterio de siempre (Planned_Shift_for_OEE != 0).
    /// </summary>
    public bool CountsForStats => HeijunkaPlanned ?? (PlannedShift > 0);

    /// <summary>
    /// Heijunka dice que esta línea NO tenía plan este turno, pero sí produjo algo —
    /// "producción no planeada". No cuenta para nada, pero se muestra en rojo en el detalle
    /// para que se entienda qué pasó.
    /// </summary>
    public bool IsUnplannedProduction => HeijunkaPlanned == false && Total > 0;
}

/// <summary>
/// Regla única (compartida por el dashboard general y los 6 de detalle) para decidir qué
/// líneas NO participan en el % de SAP: cualquier línea cuyo nombre incluya "CB" (como
/// palabra completa, ej. "... CB", "... CB 2"), "Clam Shell" o "CM1".
/// </summary>
public static class SapRules
{
    public static bool IsExcluded(string productDesc)
    {
        if (string.IsNullOrWhiteSpace(productDesc)) return false;
        if (productDesc.Contains("Clam Shell", StringComparison.OrdinalIgnoreCase)) return true;
        if (productDesc.Contains("CM1", StringComparison.OrdinalIgnoreCase)) return true;

        var words = productDesc.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => string.Equals(w, "CB", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Agregado por horno (Furnace 1..5) — Product_Group_ID 1 y 6 -> Furnace 1 (clam shells
/// comparten horno), 2 -> Furnace 2, 3 -> Furnace 3, 4 -> Furnace 4, 5 -> Furnace 5.
/// Product_Group_ID 7 (Tube Mills) se excluye en todo el dashboard.
/// Lines siempre viene ordenado ascendente por Product_Order.
/// </summary>
public class FurnaceMetric
{
    public int FurnaceId { get; set; }
    public string FurnaceName { get; set; } = string.Empty;

    /// <summary>TODAS las líneas del turno actual, incluidas las que no tienen plan
    /// (Planned_Shift_for_OEE = 0) — esas se muestran igual en el detalle pero no cuentan
    /// para nada (ver CountedLines).</summary>
    public List<ProductLineMetric> Lines { get; set; } = new();

    /// <summary>Solo las líneas que SÍ cuentan (ver ProductLineMetric.CountsForStats — Heijunka
    /// si hay datos esta semana, si no el criterio viejo de Planned_Shift_for_OEE != 0).</summary>
    private List<ProductLineMetric> CountedLines => Lines.Where(l => l.CountsForStats).ToList();

    /// <summary>De las que cuentan, solo las que YA tienen al menos 1 pieza — son las únicas
    /// que entran al promedio de OEE ponderado (ver Oee). Un turno planeado en 0 piezas no
    /// debe jalar el promedio hacia abajo hasta que arranque; en cuanto hace 1 pieza, sí cuenta.</summary>
    private List<ProductLineMetric> LinesWithProduction => CountedLines.Where(l => l.Total > 0).ToList();

    /// <summary>Líneas con producción NO planeada según Heijunka (Total > 0 pero Heijunka
    /// dice que no tenían plan este turno) — no cuentan para nada, se muestran en rojo
    /// solo en el detalle.</summary>
    public List<ProductLineMetric> UnplannedLines => Lines.Where(l => l.IsUnplannedProduction).ToList();

    public int TotalProduction => CountedLines.Sum(l => l.Total);
    public int TotalPlanned => CountedLines.Sum(l => l.PlannedShift);
    public int TotalSap => CountedLines.Sum(l => l.TotalSap);

    /// <summary>OEE ponderado del horno — SOLO promedia líneas que ya tienen producción
    /// (ver LinesWithProduction). Una línea planeada en 0 piezas no cuenta todavía.</summary>
    public double Oee => LinesWithProduction.Count == 0 ? 0 : LinesWithProduction.Average(l => l.OeeShift);

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

    /// <summary>Línea con peor OEE de turno dentro del horno (solo entre las que tienen plan y ya produjeron).</summary>
    public ProductLineMetric? WorstLine => LinesWithProduction
        .OrderBy(l => l.OeeShift)
        .FirstOrDefault();

    // ---------- Separación por segmento (Core Builder / Back End / Clam Shells) ----------
    // Solo Furnace 1 tiene líneas de Clam Shells; en el resto de hornos ese bloque siempre
    // sale en 0 y la UI simplemente no lo muestra.
    private List<ProductLineMetric> CoreBuilderLines => CountedLines.Where(l => l.Segment == LineSegment.CoreBuilder).ToList();
    private List<ProductLineMetric> BackEndLines => CountedLines.Where(l => l.Segment == LineSegment.BackEnd).ToList();
    private List<ProductLineMetric> ClamShellLines => CountedLines.Where(l => l.Segment == LineSegment.ClamShell).ToList();

    private static double AvgOee(List<ProductLineMetric> lines)
    {
        var withProd = lines.Where(l => l.Total > 0).ToList();
        return withProd.Count == 0 ? 0 : withProd.Average(l => l.OeeShift);
    }

    public int CoreBuilderProduction => CoreBuilderLines.Sum(l => l.Total);
    public int CoreBuilderPlanned => CoreBuilderLines.Sum(l => l.PlannedShift);
    public double CoreBuilderOee => AvgOee(CoreBuilderLines);

    public int BackEndProduction => BackEndLines.Sum(l => l.Total);
    public int BackEndPlanned => BackEndLines.Sum(l => l.PlannedShift);
    public double BackEndOee => AvgOee(BackEndLines);

    /// <summary>Único segmento con SAP % — Core Builder, Clam Shells y Tube Mills nunca tienen.</summary>
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
    public double ClamShellOee => AvgOee(ClamShellLines);
}

/// <summary>
/// Punto de la serie hora por hora (Report_Group=2), sumando Total de todas las líneas
/// (excepto Tube Mills) para esa hora, del turno actual únicamente.
/// </summary>
public class HourlyPoint
{
    public string Hour { get; set; } = string.Empty; // "07:00", "08:00", ...
    public int Production { get; set; }

    /// <summary>Acumulado que "deberíamos llevar" a esta hora si el ritmo fuera perfectamente
    /// parejo a lo largo del turno (Plan × fracción de turno transcurrida a esta hora). Se
    /// calcula en ShiftTimeHelper.ApplyExpectedCumulative — nunca hardcodeado. Null cuando esa
    /// hora ya se sale del horario real del turno (ver por qué en ApplyExpectedCumulative) —
    /// así la línea amarilla simplemente no dibuja ahí, en vez de saltar de golpe al 100%.</summary>
    public int? ExpectedCumulative { get; set; }
}

/// <summary>
/// Métricos (OEE/Producción/Plan) de un subconjunto de líneas para el carrusel de la
/// barra superior del dashboard general: Core Builders, Back End, Clam Shells, Tube Mills.
/// </summary>
public class KpiGroup
{
    public string Label { get; set; } = string.Empty;
    public int TotalProduction { get; set; }
    public int TotalPlanned { get; set; }
    public double Oee { get; set; }
}

/// <summary>
/// Payload completo que se manda a la vista y se retransmite por SignalR.
/// </summary>
public class PlantDashboardSnapshot
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;

    /// <summary>Viene directo de Shift_Desc del SP — nunca hardcodeado, se detecta por hora actual.</summary>
    public string ShiftDesc { get; set; } = string.Empty;

    public List<FurnaceMetric> Furnaces { get; set; } = new();
    public List<HourlyPoint> HourlyTrend { get; set; } = new();

    /// <summary>
    /// Los 4 grupos que rotan en la barra superior, en este orden fijo:
    /// [0] Core Builders (líneas CB/CM1, SIN Clam Shells),
    /// [1] Back End (el resto de Furnace 1-5, sin Clam Shells — el único con SAP),
    /// [2] Clam Shells (Product_Group_ID = 6, solo existen en Furnace 1),
    /// [3] Tube Mills (Product_Group_ID = 7, sin distinción — nunca aparece como card en el grid).
    /// </summary>
    public List<KpiGroup> TopKpiGroups { get; set; } = new();

    /// <summary>Duración del turno actual en horas, parseada de Shift_Desc — usada para calcular
    /// la meta de producción por hora (Plan / horas de turno) en las gráficas de tendencia.</summary>
    public double ShiftDurationHours { get; set; } = 8.0;

    /// <summary>true cuando este snapshot viene del histórico (no en vivo, sin SignalR).</summary>
    public bool IsHistorical { get; set; } = false;
    public DateTime? HistoricalDate { get; set; }
    public int? HistoricalShiftId { get; set; }

    /// <summary>
    /// Furnaces incluye Furnace 1-5 Y Tube Mills (FurnaceId = 6) al final — Tube Mills solo se
    /// usa para el 6to recuadro del grid (que alterna con la tendencia cada 15s) y para el
    /// modal de detalle si se le da clic; nunca se cuenta en los totales de abajo.
    /// </summary>
    public int TotalProduction => Furnaces.Where(f => f.FurnaceId <= 5).Sum(f => f.TotalProduction);
    public int TotalPlanned => Furnaces.Where(f => f.FurnaceId <= 5).Sum(f => f.TotalPlanned);

    /// <summary>OEE ponderado de TODAS las líneas de Furnace 1-5 con producción > 0 (Tube Mills
    /// no cuenta aquí, y las líneas en 0 piezas tampoco cuentan hasta que arrancan).</summary>
    public double PlantOee
    {
        get
        {
            var allLines = Furnaces.Where(f => f.FurnaceId <= 5).SelectMany(f => f.Lines)
                .Where(l => l.CountsForStats && l.Total > 0).ToList();
            return allLines.Count == 0 ? 0 : allLines.Average(l => l.OeeShift);
        }
    }
}
