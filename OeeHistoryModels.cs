namespace Metrics_Dashboard.Models;

public enum OeeHistoryLevel { Shift, Daily, Weekly, Monthly }

/// <summary>Una barra de la gráfica principal (un turno, un día, una semana o un mes).</summary>
public class OeeHistoryBar
{
    public string Label { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public int? ShiftId { get; set; } // solo aplica al nivel Turno
    public double Oee { get; set; }
    public int TotalProduction { get; set; }
    public int TotalPlanned { get; set; }

    /// <summary>El turno/día/semana/mes que está en curso ahora mismo — sus números son en vivo.</summary>
    public bool IsCurrent { get; set; }
}

/// <summary>Una barra de la sub-gráfica de detalle (horno, turno, día o semana, según el nivel).</summary>
public class OeeHistoryDetailBar
{
    public string Label { get; set; } = string.Empty;
    public double Oee { get; set; }
    public int TotalProduction { get; set; }
    public int TotalPlanned { get; set; }
}

/// <summary>
/// Fila cruda del modo @Get_Monthly_Report=1 del SP — trae un renglón por (línea, mes) para
/// TODO el año de @Start_Date en una sola llamada. Forma distinta a RawMetricRow (día/turno)
/// porque el SP arma este modo con columnas propias (mes en vez de hora/turno).
/// </summary>
public record MonthlyRawRow(
    int ReportGroup,
    int GroupId,
    string Desc,
    int PlannedShift,
    string MonthName,
    int MonthNumber,
    int AccumulatedRate,
    int Total,
    int TotalSap,
    int PlannedForOee,
    double Oee);
