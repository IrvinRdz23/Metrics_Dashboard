using System.Data.SqlClient;
using Metrics_Dashboard.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Metrics_Dashboard.Services;

public interface IMetricsRawDataService
{
    /// <summary>Filas del turno en curso (ya filtradas a ese turno) y su Shift_Desc.</summary>
    Task<(List<RawMetricRow> Rows, string ShiftDesc)> FetchCurrentShiftRowsAsync(CancellationToken ct = default);

    /// <summary>Todas las filas (todos los turnos) del reporte diario de una fecha.</summary>
    Task<List<RawMetricRow>> FetchDayRowsAsync(DateTime date, CancellationToken ct = default);

    /// <summary>
    /// Para el histórico: turnos de la fecha y cuál usar. Si no se pide uno, el último turno ya
    /// cerrado de ese día (o el primero si todavía no cierra ninguno).
    /// </summary>
    Task<(int ShiftId, List<ShiftInfo> Shifts)> ResolveShiftAsync(DateTime date, int? shiftId, CancellationToken ct = default);
}

/// <summary>
/// Único punto que habla con [dbo].[Plant_Metrics_Production_Reports]. Un solo llamado por ciclo
/// (con caché corta) alimenta el dashboard general, los detalles de grupo y las TVs de línea.
/// Solo se conservan los Product_Group_ID del catálogo de Planta 4 (8-11).
/// </summary>
public class MetricsRawDataService : IMetricsRawDataService
{
    private readonly string _connectionString;
    private readonly PlantOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MetricsRawDataService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MetricsRawDataService(IConfiguration config, IOptions<PlantOptions> options, IMemoryCache cache, ILogger<MetricsRawDataService> logger)
    {
        _connectionString = config.GetConnectionString("M2SReportServices") ?? string.Empty;
        _options = options.Value;
        _cache = cache;
        _logger = logger;
    }

    public async Task<(List<RawMetricRow> Rows, string ShiftDesc)> FetchCurrentShiftRowsAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var today = await FetchDayRowsAsync(now.Date, ct);
        var todayShifts = today.DistinctShifts();

        // 1) Turno activo que pertenece al reporte de hoy. Si hay traslape (ej. turno 1 termina 15:20 y
        //    el 2 arranca 15:15) gana el que arrancó más tarde.
        var active = todayShifts
            .Where(s => ShiftTimeHelper.IsActiveToday(s.ShiftDesc, now))
            .OrderByDescending(s => ShiftTimeHelper.StartOf(s.ShiftDesc))
            .FirstOrDefault();
        if (active != null) return (OnlyShift(today, active.ShiftId), active.ShiftDesc);

        // 2) Madrugada de un turno que cruza medianoche (ej. 05:30 del turno 23:20-06:00): su reporte es el de AYER.
        var yesterday = await FetchDayRowsAsync(now.Date.AddDays(-1), ct);
        var tail = yesterday.DistinctShifts().FirstOrDefault(s => ShiftTimeHelper.IsInMidnightCrossingTail(s.ShiftDesc, now));
        if (tail != null) return (OnlyShift(yesterday, tail.ShiftId), tail.ShiftDesc);

        // 3) Hueco entre turnos: el último que ya arrancó hoy (o el primero del día).
        var fallback = todayShifts
            .Where(s => ShiftTimeHelper.StartOf(s.ShiftDesc) <= now.TimeOfDay)
            .OrderByDescending(s => ShiftTimeHelper.StartOf(s.ShiftDesc))
            .FirstOrDefault() ?? todayShifts.FirstOrDefault();
        return fallback == null
            ? (new List<RawMetricRow>(), string.Empty)
            : (OnlyShift(today, fallback.ShiftId), fallback.ShiftDesc);
    }

    public async Task<(int ShiftId, List<ShiftInfo> Shifts)> ResolveShiftAsync(DateTime date, int? shiftId, CancellationToken ct = default)
    {
        var rows = await FetchDayRowsAsync(date.Date, ct);
        var shifts = rows.DistinctShifts();
        if (shiftId.HasValue && shifts.Any(s => s.ShiftId == shiftId.Value)) return (shiftId.Value, shifts);

        var now = DateTime.Now;
        var chosen = shifts.LastOrDefault(s => ShiftTimeHelper.HasEnded(s.ShiftDesc, date, now)) ?? shifts.FirstOrDefault();
        return (chosen?.ShiftId ?? 0, shifts);
    }

    public async Task<List<RawMetricRow>> FetchDayRowsAsync(DateTime date, CancellationToken ct = default)
    {
        date = date.Date;
        var key = $"raw|{_options.PlantListId}|{date:yyyyMMdd}";
        if (_cache.TryGetValue(key, out List<RawMetricRow>? cached) && cached != null) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached != null) return cached;

            var rows = _options.UseDemoData
                ? DemoData.BuildDayRows(date, DateTime.Now)
                : await QueryStoredProcedureAsync(date, ct);

            var ttl = date == DateTime.Today ? Math.Max(1, _options.LiveCacheSeconds) : 60;
            _cache.Set(key, rows, TimeSpan.FromSeconds(ttl));
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<RawMetricRow> OnlyShift(List<RawMetricRow> rows, int shiftId) =>
        rows.Where(r => r.ShiftId == shiftId).ToList();

    private async Task<List<RawMetricRow>> QueryStoredProcedureAsync(DateTime date, CancellationToken ct)
    {
        var rows = new List<RawMetricRow>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("[dbo].[Plant_Metrics_Production_Reports]", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 60
        };
        cmd.Parameters.AddWithValue("@Start_Date", date);
        cmd.Parameters.AddWithValue("@Plant_List_ID", _options.PlantListId);
        cmd.Parameters.AddWithValue("@Get_Daily_Report", true);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        int oRg = reader.GetOrdinal("Report_Group");
        int oGroup = reader.GetOrdinal("Product_Group_ID");
        int oListId = reader.GetOrdinal("Product_List_ID");
        int oOrder = reader.GetOrdinal("Product_Order");
        int oDesc = reader.GetOrdinal("Product_Desc");
        int oCt = reader.GetOrdinal("Cycle_Time_Secs");
        int oPlan = reader.GetOrdinal("Planned_Shift_for_OEE");
        int oAccum = reader.GetOrdinal("Accumulated_Rate");
        int oOee = reader.GetOrdinal("OEE_Shift");
        int oTotal = reader.GetOrdinal("Total");
        int oSap = reader.GetOrdinal("Total_SAP");
        int oHour = reader.GetOrdinal("Hour_by_Hour");
        int oShiftId = reader.GetOrdinal("Shift_ID");
        int oShiftDesc = reader.GetOrdinal("Shift_Desc");
        int oDate = reader.GetOrdinal("Event_Date_Short");

        while (await reader.ReadAsync(ct))
        {
            try
            {
                var groupId = Int(reader, oGroup);
                if (!ProductGroupCatalog.IsValid(groupId)) continue;

                rows.Add(new RawMetricRow(
                    ReportGroup: Int(reader, oRg),
                    GroupId: groupId,
                    Desc: Str(reader, oDesc),
                    ProductOrder: Int(reader, oOrder),
                    ProductListId: Int(reader, oListId),
                    CycleTimeSecs: Dbl(reader, oCt),
                    PlannedForOee: Int(reader, oPlan),
                    AccumRate: Int(reader, oAccum),
                    OeeShift: Dbl(reader, oOee),
                    Total: Int(reader, oTotal),
                    TotalSap: Int(reader, oSap),
                    Hour: Str(reader, oHour),
                    ShiftId: Int(reader, oShiftId),
                    ShiftDesc: Str(reader, oShiftDesc),
                    EventDateShort: Str(reader, oDate)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fila del SP ignorada por formato inesperado.");
            }
        }

        return rows;
    }

    private static int Int(SqlDataReader r, int o) => r.IsDBNull(o) ? 0 : Convert.ToInt32(r.GetValue(o));
    private static double Dbl(SqlDataReader r, int o) => r.IsDBNull(o) ? 0 : Convert.ToDouble(r.GetValue(o));
    private static string Str(SqlDataReader r, int o) => r.IsDBNull(o) ? string.Empty : Convert.ToString(r.GetValue(o)) ?? string.Empty;
}

/// <summary>Datos de mentira (PlantMetrics:UseDemoData = true) para probar todo sin SQL Server.</summary>
public static class DemoData
{
    private static readonly (string Shift, string Desc)[] Shifts =
    {
        ("1", "Shift 1 06:00-15:20"),
        ("2", "Shift 2 15:15-23:25"),
        ("3", "Shift 3 23:20-06:00"),
    };

    private static readonly Dictionary<int, string[]> Lines = new()
    {
        [8] = new[] { "PTC BMW Condenser", "PTC Honda TG7 Condenser", "PTC Honda T90 Condenser", "PTC Honda Civic Condenser", "PTC Rivian Condenser", "PTC BEV3 Condenser", "PTC Honda MDX Condenser" },
        [9] = new[] { "BCP Tesla Y Plate", "BCP BMW Plate", "BCP GM Plate", "BCP Rivian Plate" },
        [10] = new[] { "HC Toyota Heater Core", "HC GM Heater Core", "HC Honda Heater Core", "HC Ford Heater Core", "HC Nissan Heater Core" },
        [11] = new[] { "RES Radiator A", "RES Radiator B", "RES Radiator C" },
    };

    public static List<RawMetricRow> BuildDayRows(DateTime date, DateTime now)
    {
        var rows = new List<RawMetricRow>();
        var dateShort = date.ToString("yyyy/MM/dd");

        foreach (var (shiftId, desc) in Shifts)
        {
            int sid = int.Parse(shiftId);
            var start = date + ShiftTimeHelper.StartOf(desc);
            var durationMin = ShiftTimeHelper.GetDurationMinutes(desc);
            var frac = Math.Clamp((now - start).TotalMinutes / durationMin, 0, 1);
            var buckets = ShiftTimeHelper.BuildBuckets(desc);

            foreach (var (groupId, names) in Lines)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    var name = names[i];
                    var rnd = new Random(HashCode.Combine(date.DayOfYear, sid, groupId, i));
                    var listId = groupId * 100 + i + 1;
                    var ct = Math.Round(25 + rnd.NextDouble() * 55, 2);
                    var planned = (int)(durationMin * 60 * 0.8 / ct);
                    var accum = (int)Math.Round(planned * frac);
                    var eff = 0.55 + rnd.NextDouble() * 0.6;
                    var total = i == names.Length - 1 && groupId == 11 ? 0 : (int)(accum * eff);
                    var oee = accum <= 0 ? 0 : (double)total / accum;
                    var sap = (int)(total * (0.6 + rnd.NextDouble() * 0.35));
                    var order = (groupId - 7) * 100 + (i + 1) * 10;

                    rows.Add(new RawMetricRow(1, groupId, name, order, 0, ct, planned, accum, oee, total, 0, "-", sid, desc, dateShort));
                    rows.Add(new RawMetricRow(3, groupId, name, order, listId, ct, 0, 0, 0, 0, sap, "-", sid, desc, dateShort));

                    // Producción repartida en las horas ya transcurridas (con algo de ruido).
                    var elapsed = durationMin * frac;
                    var live = buckets.Where(b => b.StartMin < elapsed).ToList();
                    var weights = live.Select(b => (Math.Min(b.EndMin, elapsed) - b.StartMin) * (0.7 + rnd.NextDouble() * 0.6)).ToList();
                    var wsum = weights.Sum();
                    int assigned = 0;
                    for (int h = 0; h < live.Count; h++)
                    {
                        var v = h == live.Count - 1 ? total - assigned : (int)Math.Round(total * weights[h] / (wsum <= 0 ? 1 : wsum));
                        v = Math.Max(0, v);
                        assigned += v;
                        rows.Add(new RawMetricRow(2, groupId, name, order, listId, ct, 0, accum, 0, v, 0, live[h].Label, sid, desc, dateShort));
                    }
                }
            }
        }
        return rows;
    }
}
