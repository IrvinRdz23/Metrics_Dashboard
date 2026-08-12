using Metrics_Dashboard.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Metrics_Dashboard.Services;

public interface IOeeHistoryService
{
    Task<List<OeeHistoryBar>> GetShiftHistoryAsync(int count, CancellationToken ct = default);
    Task<List<OeeHistoryBar>> GetDailyHistoryAsync(int days, CancellationToken ct = default);
    Task<List<OeeHistoryBar>> GetWeeklyHistoryAsync(int weeks, CancellationToken ct = default);
    Task<List<OeeHistoryBar>> GetMonthlyHistoryAsync(int months, CancellationToken ct = default);

    Task<List<OeeHistoryDetailBar>> GetShiftDetailAsync(DateTime date, int shiftId, CancellationToken ct = default);
    Task<List<OeeHistoryDetailBar>> GetDailyDetailAsync(DateTime date, CancellationToken ct = default);
    Task<List<OeeHistoryDetailBar>> GetWeeklyDetailAsync(DateTime anyDateInWeek, CancellationToken ct = default);
    Task<List<OeeHistoryDetailBar>> GetMonthlyDetailAsync(int year, int month, CancellationToken ct = default);
}

/// <summary>
/// Arma las 4 graficas de OEE historico (Turno/Diario/Semanal/Mensual) y su detalle.
///
/// RENDIMIENTO: para CUALQUIER dia que ya paso, los datos salen de la tabla
/// PlantMetrics_OeeHistory (rapidisimo, un SELECT indexado) en vez de llamar al SP.
/// El SP pesado SOLO se llama para HOY (el turno/dia/semana/mes en curso, que sigue
/// siendo en vivo). OeeHistoryBackfillService es quien va llenando esa tabla poco a
/// poco en segundo plano - mientras el backfill no haya llegado a un dia todavia,
/// este servicio cae de regreso al SP para ese dia especifico (mas lento, pero nunca
/// se queda sin datos).
///
/// Ya no se usan los modos @Get_Weekly_Report/@Get_Monthly_Report del SP: semanal y
/// mensual ahora se arman sumando dias individuales (la misma fuente ya probada de
/// Turno/Diario), lo cual tambien quita el riesgo de que el formato de esos 2 modos
/// no fuera exactamente el que yo habia asumido.
/// </summary>
public class OeeHistoryService : IOeeHistoryService
{
    private readonly IMetricsRawDataService _rawDataService;
    private readonly IOeeHistoryStorageService _storage;
    private readonly ILogger<OeeHistoryService> _logger;

    private static readonly Regex ShiftTimeRegex = new(@"(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);
    private static readonly string[] SpanishDays = { "Dom", "Lun", "Mar", "Miér", "Jue", "Vie", "Sáb" };
    private static readonly string[] SpanishMonths =
    {
        "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
        "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"
    };

    // Cuantas consultas de dia se hacen AL MISMO TIEMPO. La mayoria son lecturas rapidas de
    // la tabla; esto solo importa de verdad para el dia de HOY (que sigue yendo al SP).
    private const int MaxParallelFetches = 6;

    public OeeHistoryService(IMetricsRawDataService rawDataService, IOeeHistoryStorageService storage, ILogger<OeeHistoryService> logger)
    {
        _rawDataService = rawDataService;
        _storage = storage;
        _logger = logger;
    }

    // ============================== NIVEL TURNO ==============================

    public async Task<List<OeeHistoryBar>> GetShiftHistoryAsync(int count, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var now = DateTime.Now;
        var daysNeeded = Math.Max(1, (int)Math.Ceiling(count / 3.0));
        var dates = Enumerable.Range(0, daysNeeded).Select(i => today.AddDays(-i)).ToList();

        var rowsByDate = await FetchDaysAsync(dates, ct);

        var bars = new List<OeeHistoryBar>();
        foreach (var date in dates)
        {
            var rows = rowsByDate.TryGetValue(date, out var r) ? r : new List<RawMetricRow>();
            if (rows.Count == 0) continue;

            foreach (var shiftId in new[] { 1, 2, 3 })
            {
                var shiftRows = rows.Where(x => x.ReportGroup == 1 && x.ShiftId == shiftId && x.PlannedForOee != 0).ToList();
                if (shiftRows.Count == 0) continue;

                var shiftDesc = rows.FirstOrDefault(x => x.ShiftId == shiftId)?.ShiftDesc ?? "";
                bars.Add(new OeeHistoryBar
                {
                    Label = FormatShortDate(date) + " T" + shiftId,
                    Date = date,
                    ShiftId = shiftId,
                    Oee = shiftRows.Average(x => x.OeeShift),
                    TotalProduction = shiftRows.Sum(x => x.Total),
                    TotalPlanned = shiftRows.Sum(x => x.PlannedForOee),
                    IsCurrent = date.Date == now.Date && IsWithinShift(shiftDesc, now.TimeOfDay)
                });
            }
        }

        bars = bars.OrderBy(b => b.Date).ThenBy(b => b.ShiftId).ToList();
        return bars.Skip(Math.Max(0, bars.Count - count)).ToList();
    }

    public async Task<List<OeeHistoryDetailBar>> GetShiftDetailAsync(DateTime date, int shiftId, CancellationToken ct = default)
    {
        var rows = await GetDayDataAsync(date, ct);
        var shiftRows = rows.Where(r => r.ReportGroup == 1 && r.ShiftId == shiftId && r.PlannedForOee != 0).ToList();

        return FurnaceCatalog.Map
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var lines = shiftRows.Where(r => kv.Value.ProductGroupIds.Contains(r.GroupId)).ToList();
                return new OeeHistoryDetailBar
                {
                    Label = kv.Value.Name,
                    Oee = lines.Count == 0 ? 0 : lines.Average(r => r.OeeShift),
                    TotalProduction = lines.Sum(r => r.Total),
                    TotalPlanned = lines.Sum(r => r.PlannedForOee)
                };
            })
            .Where(b => b.TotalPlanned > 0)
            .ToList();
    }

    // ============================== NIVEL DIARIO ==============================

    public async Task<List<OeeHistoryBar>> GetDailyHistoryAsync(int days, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var dates = Enumerable.Range(0, days).Select(i => today.AddDays(-i)).OrderBy(d => d).ToList();
        var rowsByDate = await FetchDaysAsync(dates, ct);

        return dates.Select(date => BuildDayBar(date, rowsByDate.TryGetValue(date, out var r) ? r : new List<RawMetricRow>(), today)).ToList();
    }

    public async Task<List<OeeHistoryDetailBar>> GetDailyDetailAsync(DateTime date, CancellationToken ct = default)
    {
        var rows = await GetDayDataAsync(date, ct);
        var counted = rows.Where(r => r.ReportGroup == 1 && r.PlannedForOee != 0).ToList();

        return new[] { 1, 2, 3 }.Select(shiftId =>
        {
            var lines = counted.Where(r => r.ShiftId == shiftId).ToList();
            return new OeeHistoryDetailBar
            {
                Label = "Turno " + shiftId,
                Oee = lines.Count == 0 ? 0 : lines.Average(r => r.OeeShift),
                TotalProduction = lines.Sum(r => r.Total),
                TotalPlanned = lines.Sum(r => r.PlannedForOee)
            };
        }).Where(b => b.TotalPlanned > 0).ToList();
    }

    // ============================== NIVEL SEMANAL ==============================

    public async Task<List<OeeHistoryBar>> GetWeeklyHistoryAsync(int weeks, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var thisMonday = MondayOf(today);
        var mondays = Enumerable.Range(0, weeks).Select(i => thisMonday.AddDays(-7 * i)).OrderBy(d => d).ToList();

        var allDays = mondays.SelectMany(m => Enumerable.Range(0, 7).Select(i => m.AddDays(i)))
            .Where(d => d <= today)
            .Distinct()
            .ToList();
        var rowsByDate = await FetchDaysAsync(allDays, ct);

        var bars = new List<OeeHistoryBar>();
        foreach (var monday in mondays)
        {
            var weekDays = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).Where(d => d <= today);
            var counted = weekDays
                .SelectMany(d => rowsByDate.TryGetValue(d, out var r) ? r : new List<RawMetricRow>())
                .Where(x => x.ReportGroup == 1 && x.PlannedForOee != 0)
                .ToList();

            var isoWeek = System.Globalization.ISOWeek.GetWeekOfYear(monday);
            bars.Add(new OeeHistoryBar
            {
                Label = "Sem " + isoWeek,
                Date = monday,
                Oee = counted.Count == 0 ? 0 : counted.Average(x => x.OeeShift),
                TotalProduction = counted.Sum(x => x.Total),
                TotalPlanned = counted.Sum(x => x.PlannedForOee),
                IsCurrent = today >= monday && today < monday.AddDays(7)
            });
        }

        return bars;
    }

    public async Task<List<OeeHistoryDetailBar>> GetWeeklyDetailAsync(DateTime anyDateInWeek, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var monday = MondayOf(anyDateInWeek);
        var days = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).Where(d => d <= today).ToList();
        var rowsByDate = await FetchDaysAsync(days, ct);

        var result = new List<OeeHistoryDetailBar>();
        foreach (var day in days)
        {
            var lines = (rowsByDate.TryGetValue(day, out var r) ? r : new List<RawMetricRow>())
                .Where(x => x.ReportGroup == 1 && x.PlannedForOee != 0).ToList();
            if (lines.Count == 0) continue;

            result.Add(new OeeHistoryDetailBar
            {
                Label = SpanishDays[(int)day.DayOfWeek] + " " + day.Day,
                Oee = lines.Average(x => x.OeeShift),
                TotalProduction = lines.Sum(x => x.Total),
                TotalPlanned = lines.Sum(x => x.PlannedForOee)
            });
        }
        return result;
    }

    // ============================== NIVEL MENSUAL ==============================

    public async Task<List<OeeHistoryBar>> GetMonthlyHistoryAsync(int months, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var startMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-(months - 1));

        var allDays = new List<DateTime>();
        for (int i = 0; i < months; i++)
        {
            var monthDate = startMonth.AddMonths(i);
            var daysInMonth = DateTime.DaysInMonth(monthDate.Year, monthDate.Month);
            for (int d = 1; d <= daysInMonth; d++)
            {
                var day = new DateTime(monthDate.Year, monthDate.Month, d);
                if (day <= today) allDays.Add(day);
            }
        }
        var rowsByDate = await FetchDaysAsync(allDays, ct);

        var bars = new List<OeeHistoryBar>();
        for (int i = 0; i < months; i++)
        {
            var monthDate = startMonth.AddMonths(i);
            var daysInMonth = DateTime.DaysInMonth(monthDate.Year, monthDate.Month);
            var counted = Enumerable.Range(1, daysInMonth)
                .Select(d => new DateTime(monthDate.Year, monthDate.Month, d))
                .Where(d => d <= today)
                .SelectMany(d => rowsByDate.TryGetValue(d, out var r) ? r : new List<RawMetricRow>())
                .Where(x => x.ReportGroup == 1 && x.PlannedForOee != 0)
                .ToList();

            bars.Add(new OeeHistoryBar
            {
                Label = SpanishMonths[monthDate.Month - 1].Substring(0, 3) + " " + monthDate.ToString("yy"),
                Date = monthDate,
                Oee = counted.Count == 0 ? 0 : counted.Average(x => x.OeeShift),
                TotalProduction = counted.Sum(x => x.Total),
                TotalPlanned = counted.Sum(x => x.PlannedForOee),
                IsCurrent = monthDate.Year == today.Year && monthDate.Month == today.Month
            });
        }

        return bars;
    }

    public async Task<List<OeeHistoryDetailBar>> GetMonthlyDetailAsync(int year, int month, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var days = Enumerable.Range(1, daysInMonth)
            .Select(d => new DateTime(year, month, d))
            .Where(d => d <= today)
            .ToList();

        var rowsByDate = await FetchDaysAsync(days, ct);
        var firstOfMonth = new DateTime(year, month, 1);

        var result = new List<OeeHistoryDetailBar>();
        var weekGroups = days.GroupBy(d => ((d - MondayOf(firstOfMonth)).Days) / 7);

        int weekNum = 1;
        foreach (var group in weekGroups.OrderBy(g => g.Key))
        {
            var lines = group
                .SelectMany(d => rowsByDate.TryGetValue(d, out var r) ? r : new List<RawMetricRow>())
                .Where(x => x.ReportGroup == 1 && x.PlannedForOee != 0)
                .ToList();

            if (lines.Count > 0)
            {
                result.Add(new OeeHistoryDetailBar
                {
                    Label = "Sem " + weekNum,
                    Oee = lines.Average(x => x.OeeShift),
                    TotalProduction = lines.Sum(x => x.Total),
                    TotalPlanned = lines.Sum(x => x.PlannedForOee)
                });
            }
            weekNum++;
        }
        return result;
    }

    // ============================== HELPERS ==============================

    private OeeHistoryBar BuildDayBar(DateTime date, List<RawMetricRow> rows, DateTime today)
    {
        var counted = rows.Where(r => r.ReportGroup == 1 && r.PlannedForOee != 0).ToList();
        if (counted.Count == 0)
        {
            return new OeeHistoryBar { Label = FormatShortDate(date), Date = date, IsCurrent = date == today };
        }
        return new OeeHistoryBar
        {
            Label = FormatShortDate(date),
            Date = date,
            Oee = counted.Average(r => r.OeeShift),
            TotalProduction = counted.Sum(r => r.Total),
            TotalPlanned = counted.Sum(r => r.PlannedForOee),
            IsCurrent = date == today
        };
    }

    /// <summary>Un dia PASADO -> lee de la tabla (rapido). HOY -> siempre en vivo por el SP.
    /// Si el backfill todavia no llego a un dia pasado, cae de regreso al SP como respaldo.</summary>
    /// <summary>Un día PASADO ya revisado (con o sin datos) -> lee de la tabla (rápido).
    /// Un día pasado que NUNCA se ha revisado -> se pide al SP una sola vez y se guarda para
    /// la próxima (así se "autocura": la próxima vez que alguien lo pida, ya sale de la tabla).
    /// HOY -> siempre en vivo por el SP, nunca se guarda mientras el turno/día sigue corriendo.</summary>
    private async Task<List<RawMetricRow>> GetDayDataAsync(DateTime date, CancellationToken ct)
    {
        if (date.Date < DateTime.Today)
        {
            if (await _storage.IsDayStoredAsync(date, ct))
            {
                return await _storage.GetStoredDayAsync(date, ct);
            }

            var rows = await SafeFetchDayAsync(date, ct);
            await _storage.UpsertDayAsync(date, rows, ct);
            return rows;
        }
        return await SafeFetchDayAsync(date, ct);
    }

    private async Task<Dictionary<DateTime, List<RawMetricRow>>> FetchDaysAsync(List<DateTime> dates, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<DateTime, List<RawMetricRow>>();
        await Parallel.ForEachAsync(dates.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFetches, CancellationToken = ct },
            async (date, token) => { result[date] = await GetDayDataAsync(date, token); });
        return new Dictionary<DateTime, List<RawMetricRow>>(result);
    }

    private async Task<List<RawMetricRow>> SafeFetchDayAsync(DateTime date, CancellationToken ct)
    {
        try { return await _rawDataService.FetchDayRowsAsync(date, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Error obteniendo dia {Date} para OEE historico", date); return new(); }
    }

    private static DateTime MondayOf(DateTime date) => date.AddDays(-(int)((7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7));

    private static string FormatShortDate(DateTime d) => SpanishDays[(int)d.DayOfWeek] + " " + d.Day.ToString("00");

    private static bool IsWithinShift(string shiftDesc, TimeSpan now)
    {
        var m = ShiftTimeRegex.Match(shiftDesc ?? string.Empty);
        if (!m.Success) return false;
        var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        var end = new TimeSpan(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0);
        return end <= start ? (now >= start || now < end) : (now >= start && now < end);
    }
}
