namespace PlantMetricsDashboard.Models;

/// <summary>
/// Representa una línea/producto individual dentro de un horno (ej. "PTC Clam Shell 1").
/// Viene de Report_Group=1 (día/turno acumulado) + Report_Group=3 (SAP) del SP.
/// </summary>
public class ProductLineMetric
{
    public string ProductDesc { get; set; } = string.Empty;
    public double CycleTimeSecs { get; set; }

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
}

/// <summary>
/// Agregado por horno (Furnace 1..5) — Product_Group_ID 1 y 6 -> Furnace 1 (clam shells
/// comparten horno), 2 -> Furnace 2, 3 -> Furnace 3, 4 -> Furnace 4, 5 -> Furnace 5.
/// Product_Group_ID 7 (Tube Mills) se excluye en todo el dashboard.
/// </summary>
public class FurnaceMetric
{
    public int FurnaceId { get; set; }
    public string FurnaceName { get; set; } = string.Empty;
    public List<ProductLineMetric> Lines { get; set; } = new();

    public int TotalProduction => Lines.Sum(l => l.Total);
    public int TotalPlanned => Lines.Sum(l => l.PlannedShift);
    public int TotalSap => Lines.Sum(l => l.TotalSap);

    /// <summary>OEE promedio de todas las líneas del horno (no producción/plan del horno).</summary>
    public double Oee => Lines.Count == 0 ? 0 : Lines.Average(l => l.OeeShift);

    public double SapPercent => TotalProduction <= 0 ? 0 : (double)TotalSap / TotalProduction;

    /// <summary>Línea con peor OEE de turno dentro del horno — para alertas rápidas.</summary>
    public ProductLineMetric? WorstLine => Lines
        .OrderBy(l => l.OeeShift)
        .FirstOrDefault();
}

/// <summary>
/// Punto de la serie hora por hora (Report_Group=2), sumando Total de todas las líneas
/// (excepto Tube Mills) para esa hora. No incluye "plan" — no existe un plan por hora real.
/// </summary>
public class HourlyPoint
{
    public string Hour { get; set; } = string.Empty; // "07:00", "08:00", ...
    public int Production { get; set; }
}

/// <summary>
/// Payload completo que se manda a la vista y se retransmite por SignalR.
/// </summary>
public class PlantDashboardSnapshot
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;

    /// <summary>Viene directo de Shift_Desc del SP — nunca hardcodeado, el SP ya resuelve el turno por hora.</summary>
    public string ShiftDesc { get; set; } = string.Empty;

    public List<FurnaceMetric> Furnaces { get; set; } = new();
    public List<HourlyPoint> HourlyTrend { get; set; } = new();

    public int TotalProduction => Furnaces.Sum(f => f.TotalProduction);
    public int TotalPlanned => Furnaces.Sum(f => f.TotalPlanned);

    /// <summary>OEE promedio de TODAS las líneas de TODOS los hornos (simple, no ponderado).</summary>
    public double PlantOee
    {
        get
        {
            var allLines = Furnaces.SelectMany(f => f.Lines).ToList();
            return allLines.Count == 0 ? 0 : allLines.Average(l => l.OeeShift);
        }
    }
}
