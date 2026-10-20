using System.Globalization;
using System.Text.RegularExpressions;
using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

/// <summary>Una hora "cubeta" del turno: Label = cierre de hora ("07:00"), offsets en minutos desde el inicio del turno.</summary>
public record ShiftBucket(string Label, double StartMin, double EndMin);

/// <summary>
/// Todo lo que tiene que ver con horarios de turno. El turno NUNCA se hardcodea: se lee del
/// Shift_Desc que regresa el SP (ej. "Shift 1 06:00-15:20").
/// </summary>
public static class ShiftTimeHelper
{
    private static readonly Regex ShiftRx =
        new(@"(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    public static bool TryParse(string? desc, out TimeSpan start, out TimeSpan end)
    {
        start = TimeSpan.Zero;
        end = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(desc)) return false;
        var m = ShiftRx.Match(desc);
        if (!m.Success) return false;
        start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        end = new TimeSpan(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0);
        return true;
    }

    public static TimeSpan StartOf(string? desc) => TryParse(desc, out var s, out _) ? s : TimeSpan.Zero;

    public static double GetDurationMinutes(string? desc)
    {
        if (!TryParse(desc, out var s, out var e)) return 480;
        var d = (e - s).TotalMinutes;
        if (d <= 0) d += 1440;
        return d;
    }

    public static double GetDurationHours(string? desc) => GetDurationMinutes(desc) / 60.0;

    public static bool CrossesMidnight(string? desc) => TryParse(desc, out var s, out var e) && e <= s;

    /// <summary>¿La hora del reloj cae dentro del rango del turno (sin importar de qué día es)?</summary>
    public static bool IsWithinShift(string? desc, DateTime now)
    {
        if (!TryParse(desc, out var s, out var e)) return false;
        var t = now.TimeOfDay;
        return e > s ? (t >= s && t < e) : (t >= s || t < e);
    }

    /// <summary>
    /// Turno que cruza medianoche y ya estamos en su "cola" (ej. 05:30 de un turno 23:20-06:00):
    /// ese turno pertenece al reporte de AYER, no al de hoy.
    /// </summary>
    public static bool IsInMidnightCrossingTail(string? desc, DateTime now) =>
        CrossesMidnight(desc) && TryParse(desc, out _, out var e) && now.TimeOfDay < e;

    /// <summary>Turno activo ahora que pertenece al reporte de HOY (excluye la cola de un turno de ayer).</summary>
    public static bool IsActiveToday(string? desc, DateTime now) =>
        IsWithinShift(desc, now) && !IsInMidnightCrossingTail(desc, now);

    /// <summary>¿El turno que arrancó el día <paramref name="shiftDay"/> ya terminó?</summary>
    public static bool HasEnded(string? desc, DateTime shiftDay, DateTime now)
    {
        if (!TryParse(desc, out var s, out _)) return false;
        var endDt = shiftDay.Date + s + TimeSpan.FromMinutes(GetDurationMinutes(desc));
        return now >= endDt;
    }

    /// <summary>"7:00" / "07:00:00" -> "07:00".</summary>
    public static string NormalizeHour(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        if (TimeSpan.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var ts))
            return $"{ts.Hours:00}:{ts.Minutes:00}";
        return s.Trim();
    }

    /// <summary>Minutos transcurridos del turno ahora mismo (0 si aún no arranca).</summary>
    public static double ElapsedNowMinutes(string? desc, DateTime now)
    {
        var m = (now.TimeOfDay - StartOf(desc)).TotalMinutes;
        if (m < 0) m += 1440;
        return Math.Min(m, GetDurationMinutes(desc));
    }

    /// <summary>
    /// Minutos desde el inicio del turno hasta una etiqueta de hora. Una diferencia negativa
    /// grande (&lt; -12h) es del día siguiente (+24h); una pequeña es la hora parcial previa al
    /// arranque (ej. cubeta "15:00" de un turno que arranca 15:15) y se queda en 0.
    /// </summary>
    public static double ElapsedSinceShiftStart(string label, string? desc)
    {
        if (!TimeSpan.TryParse(NormalizeHour(label), CultureInfo.InvariantCulture, out var ts)) return double.MaxValue;
        var diff = (ts - StartOf(desc)).TotalMinutes;
        if (diff < -720) return diff + 1440;
        return diff < 0 ? 0 : diff;
    }

    /// <summary>Las cubetas de hora del turno: la primera cierra en la siguiente hora en punto, y así hasta cubrir el fin.</summary>
    public static List<ShiftBucket> BuildBuckets(string? desc)
    {
        var list = new List<ShiftBucket>();
        if (!TryParse(desc, out var start, out _)) return list;

        var duration = GetDurationMinutes(desc);
        var startMinuteOfHour = start.TotalMinutes % 60;
        var firstEnd = startMinuteOfHour == 0 ? 60 : 60 - startMinuteOfHour;

        double startOff = 0;
        double endOff = firstEnd;
        while (startOff < duration - 0.5)
        {
            var labelMinutes = (start.TotalMinutes + endOff) % 1440;
            var label = $"{(int)(labelMinutes / 60):00}:{(int)(labelMinutes % 60):00}";
            list.Add(new ShiftBucket(label, startOff, Math.Min(endOff, duration)));
            startOff = endOff;
            endOff += 60;
        }
        return list;
    }

    /// <summary>
    /// Arma la serie hora por hora COMPLETA del turno: una cubeta por hora desde el arranque hasta
    /// la hora en curso (si es en vivo) o hasta el fin (si el turno ya cerró), con 0 donde no hubo
    /// producción. Nunca aparecen horas futuras. ExpectedCumulative = plan × fracción del turno
    /// transcurrida al cierre de cada hora (o a este instante, para la hora en curso).
    /// </summary>
    public static List<HourlyPoint> FillTrend(IEnumerable<HourlyPoint> data, string? desc, bool isLive, DateTime now, int plan)
    {
        var dict = new Dictionary<string, HourlyPoint>();
        foreach (var p in data)
        {
            var key = NormalizeHour(p.Hour);
            if (key.Length == 0) continue;
            if (dict.TryGetValue(key, out var ex))
            {
                ex.Production += p.Production;
                ex.SapProduction += p.SapProduction;
            }
            else
            {
                dict[key] = new HourlyPoint { Hour = key, Production = p.Production, SapProduction = p.SapProduction };
            }
        }

        var result = new List<HourlyPoint>();
        var used = new HashSet<string>();
        var buckets = BuildBuckets(desc);
        var duration = GetDurationMinutes(desc);
        var nowElapsed = isLive ? ElapsedNowMinutes(desc, now) : double.MaxValue;

        foreach (var b in buckets)
        {
            if (isLive && b.StartMin >= nowElapsed) break;

            dict.TryGetValue(b.Label, out var found);
            used.Add(b.Label);

            var elapsedEnd = Math.Min(b.EndMin, nowElapsed);
            result.Add(new HourlyPoint
            {
                Hour = b.Label,
                Production = found?.Production ?? 0,
                SapProduction = found?.SapProduction ?? 0,
                ExpectedCumulative = plan > 0 ? (int)Math.Round(plan * (elapsedEnd / duration)) : null
            });
        }

        // Horas con datos que no caen en ninguna cubeta (formato raro del SP): se agregan al final, en orden.
        var extras = dict.Values
            .Where(p => !used.Contains(p.Hour) && (p.Production > 0 || p.SapProduction > 0))
            .OrderBy(p => ElapsedSinceShiftStart(p.Hour, desc));
        result.AddRange(extras);

        return result;
    }
}

public interface IBreakScheduleService
{
    /// <summary>Minutos de descanso que caen entre dos offsets (minutos desde el inicio del turno).</summary>
    double OverlapMinutes(string? shiftDesc, double fromMin, double toMin);
}

/// <summary>Descansos configurables en appsettings (PlantMetrics:Breaks). Si no hay ninguno, no resta nada.</summary>
public class BreakScheduleService : IBreakScheduleService
{
    private readonly List<(TimeSpan Start, double Len)> _breaks = new();

    public BreakScheduleService(Microsoft.Extensions.Options.IOptions<PlantOptions> options)
    {
        foreach (var b in options.Value.Breaks)
        {
            if (!TimeSpan.TryParse(b.Start, CultureInfo.InvariantCulture, out var s)) continue;
            if (!TimeSpan.TryParse(b.End, CultureInfo.InvariantCulture, out var e)) continue;
            var len = (e - s).TotalMinutes;
            if (len <= 0) len += 1440;
            _breaks.Add((s, len));
        }
    }

    public double OverlapMinutes(string? shiftDesc, double fromMin, double toMin)
    {
        if (_breaks.Count == 0 || toMin <= fromMin) return 0;
        var shiftStart = ShiftTimeHelper.StartOf(shiftDesc);
        double total = 0;
        foreach (var (start, len) in _breaks)
        {
            var offset = (start - shiftStart).TotalMinutes;
            if (offset < 0) offset += 1440;
            total += Math.Max(0, Math.Min(toMin, offset + len) - Math.Max(fromMin, offset));
        }
        return total;
    }
}
