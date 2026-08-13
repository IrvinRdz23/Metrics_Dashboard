namespace Metrics_Dashboard.Models;

/// <summary>
/// Representa una línea/producto individual dentro de un horno (ej. "PTC Clam Shell 1").
/// Viene de Report_Group=1 (día/turno acumulado, turno actual, con plan > 0) + Report_Group=3 (SAP).
/// </summary>
public class ProductLineMetric
{
    public string ProductDesc { get; set; } = string.Empty;
    public double CycleTimeSecs { get; set; }

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
}

/// <summary>
/// Agregado por horno (Furnace 1..5) — Product_Group_ID 1 y 6 -> Furnace 1 (clam shells
/// comparten horno), 2 -> Furnace 2, 3 -> Furnace 3, 4 -> Furnace 4, 5 -> Furnace 5.
/// Product_Group_ID 7 (Tube Mills) se excluye en todo el dashboard.
/// Lines siempre viene ordenado ascendente por Product_Order.
/// </summary>
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

public class FurnaceMetric
{
    public int FurnaceId { get; set; }
    public string FurnaceName { get; set; } = string.Empty;

    /// <summary>TODAS las líneas del turno actual, incluidas las que no tienen plan
    /// (Planned_Shift_for_OEE = 0) — esas se muestran igual en el detalle pero no cuentan
    /// para nada (ver CountedLines).</summary>
    public List<ProductLineMetric> Lines { get; set; } = new();

    /// <summary>Solo las líneas con plan > 0 — las únicas que cuentan para producción/plan/OEE.</summary>
    private List<ProductLineMetric> CountedLines => Lines.Where(l => l.PlannedShift > 0).ToList();

    public int TotalProduction => CountedLines.Sum(l => l.Total);
    public int TotalPlanned => CountedLines.Sum(l => l.PlannedShift);
    public int TotalSap => CountedLines.Sum(l => l.TotalSap);

    /// <summary>OEE promedio de las líneas CON plan del horno (no producción/plan del horno).</summary>
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

    /// <summary>Línea con peor OEE de turno dentro del horno (solo entre las que tienen plan).</summary>
    public ProductLineMetric? WorstLine => CountedLines
        .OrderBy(l => l.OeeShift)
        .FirstOrDefault();
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
/// Payload completo que se manda a la vista y se retransmite por SignalR.
/// </summary>
/// <summary>
/// Métricos (OEE/Producción/Plan) de un subconjunto de líneas para el carrusel de la
/// barra superior del dashboard general: Core Builders, End of Line, Tube Mills.
/// </summary>
public class KpiGroup
{
    public string Label { get; set; } = string.Empty;
    public int TotalProduction { get; set; }
    public int TotalPlanned { get; set; }
    public double Oee { get; set; }
}

public class PlantDashboardSnapshot
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;

    /// <summary>Viene directo de Shift_Desc del SP — nunca hardcodeado, se detecta por hora actual.</summary>
    public string ShiftDesc { get; set; } = string.Empty;

    public List<FurnaceMetric> Furnaces { get; set; } = new();
    public List<HourlyPoint> HourlyTrend { get; set; } = new();

    /// <summary>
    /// Los 3 grupos que rotan en la barra superior, en este orden fijo:
    /// [0] Core Builders (líneas CB/Clam Shell/CM1, mismo criterio que SapRules.IsExcluded),
    /// [1] End of Line (el resto de líneas de Furnace 1-5, las que sí cuentan SAP),
    /// [2] Tube Mills (Product_Group_ID = 7, sin distinción — nunca aparece como card en el grid).
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

    /// <summary>OEE promedio de TODAS las líneas de Furnace 1-5 (Tube Mills no cuenta aquí).</summary>
    public double PlantOee
    {
        get
        {
            var allLines = Furnaces.Where(f => f.FurnaceId <= 5).SelectMany(f => f.Lines).ToList();
            return allLines.Count == 0 ? 0 : allLines.Average(l => l.OeeShift);
        }
    }
}
