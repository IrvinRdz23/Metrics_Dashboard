namespace Metrics_Dashboard.Services;

public record BreakWindow(TimeSpan Start, TimeSpan End);

public interface IBreakScheduleService
{
    /// <summary>
    /// Segundos realmente disponibles para producir en la ventana de una hora del SP
    /// (3,600 menos el traslape con los descansos configurados para ese turno).
    /// </summary>
    int GetAvailableSeconds(int shiftId, string hourLabel);
}

/// <summary>
/// Lee los horarios de descanso por turno desde appsettings.json (sección
/// PlantMetrics:BreakWindows), para poder restarlos del tiempo de ciclo real sin
/// hardcodear nada en el código — si cambian los horarios, solo se edita el JSON.
///
/// ⚠️ SOLO el Turno 1 (09:00-09:30 y 12:00-12:30) viene confirmado por Irvin. Los de
/// Turno 2 y 3 son un PLACEHOLDER razonable (mismo patrón de 2 descansos de 30 min,
/// repartidos en el turno) mientras se confirman los horarios reales — ajústalos en
/// appsettings.json en cuanto los tengas, no hace falta tocar C#.
/// </summary>
public class BreakScheduleService : IBreakScheduleService
{
    private readonly Dictionary<int, List<BreakWindow>> _breaksByShift;

    public BreakScheduleService(IConfiguration config)
    {
        _breaksByShift = new Dictionary<int, List<BreakWindow>>();

        foreach (var shiftId in new[] { 1, 2, 3 })
        {
            var windows = new List<BreakWindow>();
            var section = config.GetSection($"PlantMetrics:BreakWindows:{shiftId}");

            foreach (var child in section.GetChildren())
            {
                var startStr = child["Start"];
                var endStr = child["End"];
                if (TimeSpan.TryParse(startStr, out var start) && TimeSpan.TryParse(endStr, out var end))
                {
                    windows.Add(new BreakWindow(start, end));
                }
            }

            _breaksByShift[shiftId] = windows;
        }
    }

    public int GetAvailableSeconds(int shiftId, string hourLabel)
    {
        const int hourSeconds = 3600;

        if (!_breaksByShift.TryGetValue(shiftId, out var breaks) || breaks.Count == 0)
            return hourSeconds;

        // El Hour_by_Hour del SP es la HORA DE FIN del bucket (ej. "10:00" = producción de
        // 09:00 a 10:00), así que el bucket real es [label - 1h, label).
        if (!TimeSpan.TryParse(hourLabel, out var bucketEnd))
            return hourSeconds;

        var bucketStart = bucketEnd - TimeSpan.FromHours(1);
        if (bucketEnd == TimeSpan.Zero)
        {
            // "00:00" representa el bucket 23:00-24:00 del día.
            bucketEnd = TimeSpan.FromHours(24);
            bucketStart = TimeSpan.FromHours(23);
        }

        var overlapSeconds = breaks.Sum(b => OverlapSeconds(bucketStart, bucketEnd, b.Start, b.End));
        return Math.Max(0, hourSeconds - overlapSeconds);
    }

    /// <summary>Segundos de traslape entre dos rangos de hora del día, tolerante a rangos que cruzan medianoche.</summary>
    private static int OverlapSeconds(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd)
    {
        double aS = aStart.TotalSeconds, aE = aEnd.TotalSeconds;
        double bS = bStart.TotalSeconds, bE = bEnd.TotalSeconds;
        if (aE <= aS) aE += 86400;
        if (bE <= bS) bE += 86400;

        var overlap = Math.Max(0, Math.Min(aE, bE) - Math.Max(aS, bS));
        var overlapShifted = Math.Max(0, Math.Min(aE, bE + 86400) - Math.Max(aS, bS + 86400));
        return (int)Math.Max(overlap, overlapShifted);
    }
}
