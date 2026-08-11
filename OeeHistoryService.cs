using Metrics_Dashboard.Models;
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
/// Diseno pensado para no golpear la base de mas de lo necesario:
///   - Turno y Diario: una llamada @Get_Daily_Report por dia (cada una ya trae los 3 turnos).
///   - Semanal: una llamada @Get_Weekly_Report por SEMANA (7 dias en un solo query).
///   - Mensual: una llamada @Get_Monthly_Report por ANO (los 12 meses en un solo query).
/// El turno/dia/semana/mes que esta en curso sale "vivo" solo porque su fecha cae dentro
/// del rango que se le pide al SP - no hace falta logica extra para eso.
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

    public OeeHistoryService(IMetricsRawDataService rawDataService, ILogger<OeeHistoryService> logger)
    {
        _rawDataService = rawDataService;
        _logger = logger;
    }

    // ============================== NIVEL TURNO ==============================

    public async Task<List<OeeHistoryBar>> GetShiftHistoryAsync(int count, CancellationToken ct = default)
    {
        var bars = new List<OeeHistoryBar>();
        var day = DateTime.Today;
        var now = DateTime.Now;

        var daysNeeded = Math.Max(1, (int)Math.Ceiling(count / 3.0));

        for (int i = 0; i < daysNeeded; i++)
        {
            var rows = await SafeFetchDayAsync(day.AddDays(-i), ct);
            if (rows.Count == 0) continue;

            foreach (var shiftId in new[] { 1, 2, 3 })
            {
                var shiftRows = rows.Where(r => r.ReportGroup == 1 && r.ShiftId == shiftId && r.PlannedForOee != 0).ToList();
                if (shiftRows.Count == 0) continue;

                var shiftDesc = rows.FirstOrDefault(r => r.ShiftId == shiftId)?.ShiftDesc ?? "";
                bars.Add(new OeeHistoryBar
                {
                    Label = FormatShortDate(day.AddDays(-i)) + " T" + shiftId,
                    Date = day.AddDays(-i),
                    ShiftId = shiftId,
                    Oee = shiftRows.Average(r => r.OeeShift),
                    TotalProduction = shiftRows.Sum(r => r.Total),
                    TotalPlanned = shiftRows.Sum(r => r.PlannedForOee),
                    IsCurrent = day.AddDays(-i).Date == now.Date && IsWithinShift(shiftDesc, now.TimeOfDay)
                });
            }
        }

        bars = bars.OrderBy(b => b.Date).ThenBy(b => b.ShiftId).ToList();
        return bars.Skip(Math.Max(0, bars.Count - count)).ToList();
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

    // ============================== NIVEL DIARIO ==============================

    public async Task<List<OeeHistoryBar>> GetDailyHistoryAsync(int days, CancellationToken ct = default)
    {
        var bars = new List<OeeHistoryBar>();
        var today = DateTime.Today;

        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            var rows = await SafeFetchDayAsync(date, ct);
            var counted = rows.Where(r => r.ReportGroup == 1 && r.PlannedForOee != 0).ToList();
            if (counted.Count == 0)
            {
                bars.Add(new OeeHistoryBar { Label = FormatShortDate(date), Date = date, IsCurrent = date == today });
                continue;
            }

            bars.Add(new OeeHistoryBar
            {
                Label = FormatShortDate(date),
                Date = date,
                Oee = counted.Average(r => r.OeeShift),
                TotalProduction = counted.Sum(r => r.Total),
                TotalPlanned = counted.Sum(r => r.PlannedForOee),
                IsCurrent = date == today
            });
        }

        return bars;
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

    // ============================== NIVEL SEMANAL ==============================

    public async Task<List<OeeHistoryBar>> GetWeeklyHistoryAsync(int weeks, CancellationToken ct = default)
    {
        var bars = new List<OeeHistoryBar>();
        var today = DateTime.Today;
        var thisMonday = today.AddDays(-(int)((7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7));

        for (int i = weeks - 1; i >= 0; i--)
        {
            var mondayOfWeek = thisMonday.AddDays(-7 * i);
            var rows = await SafeFetchWeekAsync(mondayOfWeek, ct);
            var counted = rows.Where(r => r.ReportGroup == 1 && r.PlannedForOee != 0).ToList();

            var isoWeek = System.Globalization.ISOWeek.GetWeekOfYear(mondayOfWeek);
            bars.Add(new OeeHistoryBar
            {
                Label = "Sem " + isoWeek,
                Date = mondayOfWeek,
                Oee = counted.Count == 0 ? 0 : counted.Average(r => r.OeeShift),
                TotalProduction = counted.Sum(r => r.Total),
                TotalPlanned = counted.Sum(r => r.PlannedForOee),
                IsCurrent = today >= mondayOfWeek && today < mondayOfWeek.AddDays(7)
            });
        }

        return bars;
    }

    public async Task<List<OeeHistoryDetailBar>> GetWeeklyDetailAsync(DateTime anyDateInWeek, CancellationToken ct = default)
    {
        var monday = anyDateInWeek.AddDays(-(int)((7 + (anyDateInWeek.DayOfWeek - DayOfWeek.Monday)) % 7));
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

    // ============================== NIVEL MENSUAL ==============================

    public async Task<List<OeeHistoryBar>> GetMonthlyHistoryAsync(int months, CancellationToken ct = default)
    {
        var today = DateTime.Today;
        var startMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-(months - 1));

        var years = Enumerable.Range(0, months)
            .Select(i => startMonth.AddMonths(i).Year)
            .Distinct()
            .ToList();

        var byYear = new Dictionary<int, List<MonthlyRawRow>>();
        foreach (var year in years)
        {
            byYear[year] = await SafeFetchYearAsync(new DateTime(year, 6, 1), ct);
        }

        var bars = new List<OeeHistoryBar>();
        for (int i = 0; i < months; i++)
        {
            var monthDate = startMonth.AddMonths(i);
            var rows = byYear.TryGetValue(monthDate.Year, out var yearRows) ? yearRows : new List<MonthlyRawRow>();
            var counted = rows.Where(r => r.ReportGroup == 1 && r.MonthNumber == monthDate.Month && r.PlannedForOee != 0).ToList();

            bars.Add(new OeeHistoryBar
            {
                Label = SpanishMonths[monthDate.Month - 1].Substring(0, 3) + " " + monthDate.ToString("yy"),
                Date = monthDate,
                Oee = counted.Count == 0 ? 0 : counted.Average(r => r.Oee),
                TotalProduction = counted.Sum(r => r.Total),
                TotalPlanned = counted.Sum(r => r.PlannedForOee),
                IsCurrent = monthDate.Year == today.Year && monthDate.Month == today.Month
            });
        }

        return bars;
    }

    public async Task<List<OeeHistoryDetailBar>> GetMonthlyDetailAsync(int year, int month, CancellationToken ct = default)
    {
        var firstOfMonth = new DateTime(year, month, 1);
        var lastOfMonth = firstOfMonth.AddMonths(1).AddDays(-1);

        var mondays = new List<DateTime>();
        var cursor = firstOfMonth.AddDays(-(int)((7 + (firstOfMonth.DayOfWeek - DayOfWeek.Monday)) % 7));
        while (cursor <= lastOfMonth)
        {
            mondays.Add(cursor);
            cursor = cursor.AddDays(7);
        }

        var result = new List<OeeHistoryDetailBar>();
        int weekNum = 1;
        foreach (var monday in mondays)
        {
            var rows = await SafeFetchWeekAsync(monday, ct);
            var counted = rows.Where(r =>
                r.ReportGroup == 1 && r.PlannedForOee != 0 && !string.IsNullOrWhiteSpace(r.EventDateShort) &&
                TryParseEventDate(r.EventDateShort, out var d) && d.Month == month && d.Year == year).ToList();

            if (counted.Count > 0)
            {
                result.Add(new OeeHistoryDetailBar
                {
                    Label = "Sem " + weekNum,
                    Oee = counted.Average(r => r.OeeShift),
                    TotalProduction = counted.Sum(r => r.Total),
                    TotalPlanned = counted.Sum(r => r.PlannedForOee)
                });
            }
            weekNum++;
        }
        return result;
    }

    // ============================== HELPERS ==============================

    private async Task<List<RawMetricRow>> SafeFetchDayAsync(DateTime date, CancellationToken ct)
    {
        try { return await _rawDataService.FetchDayRowsAsync(date, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Error obteniendo dia {Date} para OEE historico", date); return new(); }
    }

    private async Task<List<RawMetricRow>> SafeFetchWeekAsync(DateTime monday, CancellationToken ct)
    {
        try { return await _rawDataService.FetchWeekRowsAsync(monday, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Error obteniendo semana de {Date} para OEE historico", monday); return new(); }
    }

    private async Task<List<MonthlyRawRow>> SafeFetchYearAsync(DateTime anyDateInYear, CancellationToken ct)
    {
        try { return await _rawDataService.FetchYearMonthlyRowsAsync(anyDateInYear, ct); }
        catch (Exception ex) { _logger.LogError(ex, "Error obteniendo ano de {Date} para OEE historico", anyDateInYear); return new(); }
    }

    private static bool TryParseEventDate(string eventDateShort, out DateTime date)
        => DateTime.TryParse(eventDateShort.Replace("/", "-"), out date);

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
