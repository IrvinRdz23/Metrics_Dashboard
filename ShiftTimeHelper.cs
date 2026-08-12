using Metrics_Dashboard.Models;
using System.Text.RegularExpressions;

namespace Metrics_Dashboard.Services;

/// <summary>
/// Duración del turno en horas, parseada del propio Shift_Desc (ej. "Shift 1 06:00-15:20" -> 9.33h).
/// Se usa para calcular la meta de producción por hora (Plan / horas de turno) en las gráficas
/// de tendencia — nunca se hardcodea la duración del turno, sale del mismo texto que ya
/// trae el SP.
/// </summary>
public static class ShiftTimeHelper
{
    private static readonly Regex ShiftTimeRegex = new(@"(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    public static double GetDurationHours(string? shiftDesc)
    {
        if (string.IsNullOrWhiteSpace(shiftDesc)) return 8.0;

        var m = ShiftTimeRegex.Match(shiftDesc);
        if (!m.Success) return 8.0;

        var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        var end = new TimeSpan(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0);

        var duration = end - start;
        if (duration <= TimeSpan.Zero) duration += TimeSpan.FromHours(24); // turno cruza medianoche

        return duration.TotalHours;
    }

    /// <summary>
    /// Rellena HourlyPoint.ExpectedCumulative en cada punto: el acumulado que "deberíamos
    /// llevar" a esa hora si el ritmo fuera parejo durante todo el turno (Plan × fracción de
    /// turno transcurrida). Es la línea amarilla que compite contra el acumulado real (verde)
    /// en las gráficas de tendencia — de dónde sale el inicio del turno también lo dice el
    /// propio Shift_Desc, nada hardcodeado.
    ///
    /// IMPORTANTE: para la hora que está EN CURSO ahora mismo (solo aplica si isLiveToday=true,
    /// es decir, estamos viendo el turno de HOY en vivo), se usa la hora real de "ahora" en vez
    /// de asumir que la hora ya se completó — si no, se sobreestima contra el Accumulated_Rate
    /// real que reporta el SP para ese mismo instante. Para horas ya completadas, o para
    /// cualquier turno histórico (isLiveToday=false), sí se asume la hora completa.
    /// </summary>
    public static void ApplyExpectedCumulative(List<HourlyPoint> hourlyTrend, string? shiftDesc, double shiftDurationHours, int totalPlanned, bool isLiveToday = true)
    {
        if (hourlyTrend.Count == 0 || totalPlanned <= 0 || shiftDurationHours <= 0) return;

        var m = ShiftTimeRegex.Match(shiftDesc ?? string.Empty);
        if (!m.Success) return;

        var shiftStart = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        var totalMinutes = shiftDurationHours * 60.0;
        var now = DateTime.Now.TimeOfDay;

        foreach (var point in hourlyTrend)
        {
            if (!TimeSpan.TryParse(point.Hour, out var bucketEnd)) continue;
            if (bucketEnd == TimeSpan.Zero) bucketEnd = TimeSpan.FromHours(24); // "00:00" = fin del día

            var effectiveTime = bucketEnd;

            if (isLiveToday)
            {
                var bucketStart = bucketEnd - TimeSpan.FromHours(1);
                var nowIsWithinBucket = now >= bucketStart && now < bucketEnd;
                if (nowIsWithinBucket) effectiveTime = now; // la hora sigue corriendo -> usa "ahora", no el fin de hora
            }

            var elapsed = effectiveTime - shiftStart;
            if (elapsed < TimeSpan.Zero) elapsed += TimeSpan.FromHours(24); // turno cruza medianoche

            var fraction = Math.Min(1.0, elapsed.TotalMinutes / totalMinutes);
            point.ExpectedCumulative = (int)Math.Round(totalPlanned * fraction);
        }
    }
}
