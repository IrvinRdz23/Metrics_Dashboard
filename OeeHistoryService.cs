using Metrics_Dashboard.Models;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Metrics_Dashboard.Services;

public interface IOeeHistoryService
{
    Task<List<OeeHistoryBar>> GetShiftHistoryAsync(CancellationToken ct = default);
    Task<List<OeeHistoryBar>> GetDailyHistoryAsync(CancellationToken ct = default);
    Task<List<OeeHistoryBar>> GetWeeklyHistoryAsync(CancellationToken ct = default);
    Task<List<OeeHistoryBar>> GetMonthlyHistoryAsync(CancellationToken ct = default);

    Task<List<OeeHistoryDetailBar>> GetShiftDetailAsync(DateTime date, int shiftId, CancellationToken ct = default);
    Task<List<OeeHistoryDetailBar>> GetDailyDetailAsync(DateTime date, CancellationToken ct = default);
    Task<List<OeeHistoryDetailBar>> GetWeeklyDetailAsync(DateTime anyDateInWeek, CancellationToken ct = default);
    Task<List<OeeHistoryDetailBar>> GetMonthlyDetailAsync(int year, int month, CancellationToken ct = default);
}

/// <summary>
/// Arma las 4 gráficas de OEE (Turno/Diario/Semanal/Mensual) llamando al SP directo — SIN
/// tabla ni caché de por medio (esa vía se probó y causó más problemas de los que resolvía,
/// así que se quitó por completo). Rangos fijos, alineados a calendario, no "últimos N":
///   - Turno:   los turnos de ESTA semana.
///   - Diario:  esta semana + la pasada (14 días).
///   - Semanal: todas las semanas del AÑO ACTUAL hasta hoy — usa @Get_Weekly_Report (1
///     llamada por semana, no 7 llamadas por día).
///   - Mensual: todos los meses del AÑO ACTUAL — usa @Get_Monthly_Report, que trae el año
///     completo en UNA sola llamada.
/// Las llamadas independientes se hacen en paralelo (tope configurable) para no encadenarlas
/// una tras otra.
/// </summary>
public class OeeHistoryService : IOeeHistoryService
{
    private readonly IMetricsRawDataService _rawDataService;
    private readonly ILogger<OeeHistoryService> _logger;

    private static readonly Regex ShiftTimeRegex = new(@"(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);
    private static readonly string[] SpanishDays = { "Dom", "Lun", "Mar", "Miér", "Jue", "Vie", "Sáb" };
    private static readonly string[] SpanishMonths =
    {
        "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
        "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"
    };

    private const int MaxParallelFetches = 5;

    public OeeHistoryService(IMetricsRawDataService rawDataService, ILogger<OeeHistoryService> logger)
    {
        _rawDataService = rawDataService;
        _logger = logger;
    }

    // ============================== NIVEL TURNO (esta semana) ==============================

    public async Task<List<OeeHistoryBar>> GetShiftHistoryAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var now = DateTime.Now;
        var dates = DaysOfWeekSoFar(MondayOf(today), today);

        var rowsByDate = await FetchDaysInParallelAsync(dates, ct);

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

        return bars.OrderBy(b => b.Date).ThenBy(b => b.ShiftId).ToList();
    }

    public async Task<List<OeeHistoryDetailBar>> GetShiftDetailAsync(DateTime date, int shiftId, CancellationToken ct = default)
    {
        var rows = await SafeFetchDayAsync(date, ct);
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

    // ============================== NIVEL DIARIO (esta semana + la pasada) ==============================

    public async Task<List<OeeHistoryBar>> GetDailyHistoryAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var lastMonday = MondayOf(today).AddDays(-7);
        var dates = Enumerable.Range(0, (today - lastMonday).Days + 1).Select(i => lastMonday.AddDays(i)).ToList();

        var rowsByDate = await FetchDaysInParallelAsync(dates, ct);

        return dates.Select(date =>
        {
            var rows = rowsByDate.TryGetValue(date, out var r) ? r : new List<RawMetricRow>();
            return BuildDayBar(date, rows, today);
        }).ToList();
    }

    public async Task<List<OeeHistoryDetailBar>> GetDailyDetailAsync(DateTime date, CancellationToken ct = default)
    {
        var rows = await SafeFetchDayAsync(date, ct);
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

    // ============================== NIVEL SEMANAL (año actual) ==============================

    public async Task<List<OeeHistoryBar>> GetWeeklyHistoryAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var firstMondayOfYear = MondayOf(new DateTime(today.Year, 1, 1));
        var thisMonday = MondayOf(today);

        var mondays = new List<DateTime>();
        for (var m = firstMondayOfYear; m <= thisMonday; m = m.AddDays(7)) mondays.Add(m);

        var rowsByMonday = await FetchWeeksInParallelAsync(mondays, ct);

        var bars = new List<OeeHistoryBar>();
        foreach (var monday in mondays)
        {
            var rows = rowsByMonday.TryGetValue(monday, out var r) ? r : new List<RawMetricRow>();
            var counted = rows.Where(x => x.ReportGroup == 1 && x.PlannedForOee != 0).ToList();

            var isoWeek = System.Globalization.ISOWeek.GetWeekOfYear(monday);
            bars.Add(new OeeHistoryBar
            {
                Label = "Sem " + isoWeek,
                Date = monday,
                Oee = counted.Count == 0 ? 0 : counted.Average(x => x.OeeShift),
                TotalProduction = counted.Sum(x => x.Total),
                TotalPlanned = counted.Sum(x => x.PlannedForOee),
                IsCurrent = monday == thisMonday
            });
        }

        return bars;
    }

    public async Task<List<OeeHistoryDetailBar>> GetWeeklyDetailAsync(DateTime anyDateInWeek, CancellationToken ct = default)
    {
        var monday = MondayOf(anyDateInWeek);
        var rows = await SafeFetchWeekAsync(monday, ct);
        var counted = rows.Where(r => r.ReportGroup == 1 && r.PlannedForOee != 0 && !string.IsNullOrWhiteSpace(r.EventDateShort)).ToList();

        var result = new List<OeeHistoryDetailBar>();
        for (int i = 0; i < 7; i++)
        {
            var day = monday.AddDays(i);
            var lines = counted.Where(r => TryParseEventDate(r.EventDateShort, out var d) && d.Date == day.Date).ToList();
            if (lines.Count == 0) continue;

            result.Add(new OeeHistoryDetailBar
            {
                Label = SpanishDays[(int)day.DayOfWeek] + " " + day.Day,
                Oee = lines.Average(r => r.OeeShift),
                TotalProduction = lines.Sum(r => r.Total),
                TotalPlanned = lines.Sum(r => r.PlannedForOee)
            });
        }
        return result;
    }

    // ============================== NIVEL MENSUAL (año actual, 1 sola llamada) ==============================

    public async Task<List<OeeHistoryBar>> GetMonthlyHistoryAsync(CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var rows = await SafeFetchYearAsync(new DateTime(today.Year, 6, 1), ct);

        var bars = new List<OeeHistoryBar>();
        for (int month = 1; month <= today.Month; month++)
        {
            var counted = rows.Where(r => r.ReportGroup == 1 && r.MonthNumber == month && r.PlannedForOee != 0).ToList();
            bars.Add(new OeeHistoryBar
            {
                Label = SpanishMonths[month - 1].Substring(0, 3) + " " + today.ToString("yy"),
                Date = new DateTime(today.Year, month, 1),
                Oee = counted.Count == 0 ? 0 : counted.Average(r => r.Oee),
                TotalProduction = counted.Sum(r => r.Total),
                TotalPlanned = counted.Sum(r => r.PlannedForOee),
                IsCurrent = month == today.Month
            });
        }

        return bars;
    }

    public async Task<List<OeeHistoryDetailBar>> GetMonthlyDetailAsync(int year, int month, CancellationToken ct = default)
    {
        var firstOfMonth = new DateTime(year, month, 1);
        var lastOfMonth = firstOfMonth.AddMonths(1).AddDays(-1);

        var mondays = new List<DateTime>();
        var cursor = MondayOf(firstOfMonth);
        while (cursor <= lastOfMonth)
        {
            mondays.Add(cursor);
            cursor = cursor.AddDays(7);
        }

        var rowsByMonday = await FetchWeeksInParallelAsync(mondays, ct);

        var result = new List<OeeHistoryDetailBar>();
        int weekNum = 1;
        foreach (var monday in mondays)
        {
            var rows = rowsByMonday.TryGetValue(monday, out var r) ? r : new List<RawMetricRow>();
            var counted = rows.Where(x =>
                x.ReportGroup == 1 && x.PlannedForOee != 0 && !string.IsNullOrWhiteSpace(x.EventDateShort) &&
                TryParseEventDate(x.EventDateShort, out var d) && d.Month == month && d.Year == year).ToList();

            if (counted.Count > 0)
            {
                result.Add(new OeeHistoryDetailBar
                {
                    Label = "Sem " + weekNum,
                    Oee = counted.Average(x => x.OeeShift),
                    TotalProduction = counted.Sum(x => x.Total),
                    TotalPlanned = counted.Sum(x => x.PlannedForOee)
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

    private static List<DateTime> DaysOfWeekSoFar(DateTime monday, DateTime today)
        => Enumerable.Range(0, (today - monday).Days + 1).Select(i => monday.AddDays(i)).ToList();

    private async Task<Dictionary<DateTime, List<RawMetricRow>>> FetchDaysInParallelAsync(List<DateTime> dates, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<DateTime, List<RawMetricRow>>();
        await Parallel.ForEachAsync(dates.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFetches, CancellationToken = ct },
            async (date, token) => { result[date] = await SafeFetchDayAsync(date, token); });
        return new Dictionary<DateTime, List<RawMetricRow>>(result);
    }

    private async Task<Dictionary<DateTime, List<RawMetricRow>>> FetchWeeksInParallelAsync(List<DateTime> mondays, CancellationToken ct)
    {
        var result = new ConcurrentDictionary<DateTime, List<RawMetricRow>>();
        await Parallel.ForEachAsync(mondays.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFetches, CancellationToken = ct },
            async (monday, token) => { result[monday] = await SafeFetchWeekAsync(monday, token); });
        return new Dictionary<DateTime, List<RawMetricRow>>(result);
    }

    private async Task<List<RawMetricRow>> SafeFetchDayAsync(DateTime date, CancellationToken ct)
    {
        try { return await _rawDataService.FetchDayRowsAsync(date, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Error obteniendo día {Date} para OEE histórico", date); return new(); }
    }

    private async Task<List<RawMetricRow>> SafeFetchWeekAsync(DateTime monday, CancellationToken ct)
    {
        try { return await _rawDataService.FetchWeekRowsAsync(monday, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Error obteniendo semana de {Date} para OEE histórico", monday); return new(); }
    }

    private async Task<List<MonthlyRawRow>> SafeFetchYearAsync(DateTime anyDateInYear, CancellationToken ct)
    {
        try { return await _rawDataService.FetchYearMonthlyRowsAsync(anyDateInYear, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Error obteniendo año de {Date} para OEE histórico", anyDateInYear); return new(); }
    }

    private static bool TryParseEventDate(string eventDateShort, out DateTime date)
        => DateTime.TryParse(eventDateShort.Replace("/", "-"), out date);

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
