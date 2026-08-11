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
}
