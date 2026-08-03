namespace PlantMetricsDashboard.Models;

/// <summary>
/// Representa una línea/producto individual dentro de un horno (ej. "PTC Clam Shell 1").
/// Mapea 1:1 con las filas de detalle del reporte por correo.
/// </summary>
public class ProductLineMetric
{
    public int ProductListId { get; set; }
    public string ProductDesc { get; set; } = string.Empty;
    public double CycleTimeSecs { get; set; }
    public double CutMin { get; set; }
    public int PlannedShift { get; set; }
    public int Total { get; set; }
    public int TotalSap { get; set; }

    /// <summary>OEE de turno = Total / PlannedShift (0 a 1+)</summary>
    public double OeeShift => PlannedShift <= 0
        ? (Total > 0 ? 1.0 : 0.0)
        : (double)Total / PlannedShift;

    public double SapPercent => Total <= 0 ? 0 : (double)TotalSap / Total;
}

/// <summary>
/// Agregado por horno (Furnace 1..5) — lo que alimenta cada card / dashboard individual.
/// </summary>
public class FurnaceMetric
{
    public int FurnaceId { get; set; }
    public string FurnaceName { get; set; } = string.Empty;
    public List<ProductLineMetric> Lines { get; set; } = new();

    public int TotalProduction => Lines.Sum(l => l.Total);
    public int TotalPlanned => Lines.Sum(l => l.PlannedShift);
    public int TotalSap => Lines.Sum(l => l.TotalSap);

    public double Oee => TotalPlanned <= 0
        ? (TotalProduction > 0 ? 1.0 : 0.0)
        : (double)TotalProduction / TotalPlanned;

    public double SapPercent => TotalProduction <= 0 ? 0 : (double)TotalSap / TotalProduction;

    /// <summary>Línea con peor desempeño dentro del horno — para alertas rápidas.</summary>
    public ProductLineMetric? WorstLine => Lines
        .Where(l => l.PlannedShift > 0)
        .OrderBy(l => l.OeeShift)
        .FirstOrDefault();
}

/// <summary>
/// Punto de la serie hora por hora (para el gráfico de tendencia general).
/// </summary>
public class HourlyPoint
{
    public string Hour { get; set; } = string.Empty; // "07:00", "08:00", ...
    public int Production { get; set; }
    public int Planned { get; set; }
}

/// <summary>
/// Payload completo que se manda a la vista y se retransmite por SignalR.
/// </summary>
public class PlantDashboardSnapshot
{
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string ShiftDesc { get; set; } = string.Empty;
    public List<FurnaceMetric> Furnaces { get; set; } = new();
    public List<HourlyPoint> HourlyTrend { get; set; } = new();

    public int TotalProduction => Furnaces.Sum(f => f.TotalProduction);
    public int TotalPlanned => Furnaces.Sum(f => f.TotalPlanned);
    public double PlantOee => TotalPlanned <= 0 ? 0 : (double)TotalProduction / TotalPlanned;
}
