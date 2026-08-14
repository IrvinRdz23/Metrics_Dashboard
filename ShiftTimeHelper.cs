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
    /// Ordena los puntos de la gráfica de tendencia por MINUTOS TRANSCURRIDOS DESDE EL INICIO
    /// DEL TURNO, no alfabéticamente por el texto de la hora. Sin esto, una hora como "00:00"
    /// (el último bucket de un turno que cruza medianoche, ej. Turno 2/3) ordena ANTES que
    /// "06:00" solo por comparación de texto, aunque en realidad sea la última hora del turno.
    /// </summary>
    public static List<HourlyPoint> SortByShiftElapsed(List<HourlyPoint> points, string? shiftDesc)
    {
        var m = ShiftTimeRegex.Match(shiftDesc ?? string.Empty);
        if (!m.Success) return points.OrderBy(p => p.Hour, StringComparer.Ordinal).ToList();

        var shiftStart = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        return points.OrderBy(p => ElapsedSinceShiftStart(p.Hour, shiftStart)).ToList();
    }

    /// <summary>
    /// Rellena HourlyPoint.ExpectedCumulative en cada punto: el acumulado que "deberíamos
    /// llevar" a esa hora si el ritmo fuera parejo durante todo el turno (Plan × fracción de
    /// turno transcurrida). Es la línea amarilla que compite contra el acumulado real (verde)
    /// en las gráficas de tendencia — de dónde sale el inicio del turno también lo dice el
    /// propio Shift_Desc, nada hardcodeado.
    ///
    /// Dos casos especiales:
    /// - Hora EN CURSO ahora mismo (solo isLiveToday=true): se usa la hora real de "ahora",
    ///   no el fin de esa hora, para no sobreestimar contra el Accumulated_Rate real del SP.
    /// - Hora que se SALE del horario real del turno (ej. el turno termina 15:20 pero el
    ///   bucket de hora es "15:00-16:00"): se deja en null (hueco en la gráfica) en vez de
    ///   saltar de golpe al 100% del plan — ese salto es matemáticamente "correcto" pero se
    ///   ve como un pico confuso, así que mejor no se dibuja ese último punto.
    /// </summary>
    public static void ApplyExpectedCumulative(List<HourlyPoint> hourlyTrend, string? shiftDesc, double shiftDurationHours, int totalPlanned, bool isLiveToday = true)
    {
        if (hourlyTrend.Count == 0 || totalPlanned <= 0 || shiftDurationHours <= 0) return;

        var m = ShiftTimeRegex.Match(shiftDesc ?? string.Empty);
        if (!m.Success) return;

        var shiftStart = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        var totalMinutes = shiftDurationHours * 60.0;
        var now = DateTime.Now.TimeOfDay;

        if (isLiveToday)
        {
            // El SP arma de antemano las casillas de TODAS las horas del turno, aunque
            // todavía no hayan pasado (ej. a las 8am ya viene la casilla de "10:00" vacía).
            // Esas horas que NI SIQUIERA HAN EMPEZADO se quitan por completo del turno en
            // vivo — ni barra vacía ni línea de esperado ahí, hasta que de verdad lleguen.
            hourlyTrend.RemoveAll(p =>
            {
                if (!TimeSpan.TryParse(p.Hour, out var end)) return false;
                if (end == TimeSpan.Zero) end = TimeSpan.FromHours(24);
                var start = end - TimeSpan.FromHours(1);
                return now < start;
            });
        }

        foreach (var point in hourlyTrend)
        {
            if (!TimeSpan.TryParse(point.Hour, out var bucketEnd)) continue;
            if (bucketEnd == TimeSpan.Zero) bucketEnd = TimeSpan.FromHours(24); // "00:00" = fin del día

            var bucketStart = bucketEnd - TimeSpan.FromHours(1);
            var effectiveTime = bucketEnd;

            if (isLiveToday && now >= bucketStart && now < bucketEnd)
            {
                effectiveTime = now; // la hora sigue corriendo -> usa "ahora", no el fin de hora
            }

            var elapsedMinutes = ElapsedSinceShiftStart(effectiveTime, shiftStart).TotalMinutes;

            // Este bucket ya se sale del horario real del turno (ej. turno termina 15:20 pero
            // el bucket es 15:00-16:00) -> mejor sin dato aquí que un salto brusco al 100%.
            if (elapsedMinutes > totalMinutes + 1)
            {
                point.ExpectedCumulative = null;
                continue;
            }

            var fraction = Math.Min(1.0, elapsedMinutes / totalMinutes);
            point.ExpectedCumulative = (int)Math.Round(totalPlanned * fraction);
        }
    }

    private static TimeSpan ElapsedSinceShiftStart(string hourLabel, TimeSpan shiftStart)
    {
        if (!TimeSpan.TryParse(hourLabel, out var bucketEnd)) return TimeSpan.MaxValue;
        if (bucketEnd == TimeSpan.Zero) bucketEnd = TimeSpan.FromHours(24);
        return ElapsedSinceShiftStart(bucketEnd, shiftStart);
    }

    private static TimeSpan ElapsedSinceShiftStart(TimeSpan time, TimeSpan shiftStart)
    {
        var elapsed = time - shiftStart;
        if (elapsed < TimeSpan.Zero) elapsed += TimeSpan.FromHours(24); // turno cruza medianoche
        return elapsed;
    }
}
