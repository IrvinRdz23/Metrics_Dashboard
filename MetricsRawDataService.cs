using Metrics_Dashboard.Models;
using System.Data.SqlClient;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Metrics_Dashboard.Services;

public interface IMetricsRawDataService
{
    /// <summary>
    /// Ejecuta [dbo].[Plant_Metrics_Production_Reports] para HOY y regresa todas las filas
    /// del turno vigente (detectado por hora real contra el rango en Shift_Desc), de
    /// TODOS los Product_Group_ID (incluyendo el 7 / Tube Mills — cada consumidor decide
    /// si lo usa o lo excluye). De aquí se derivan tanto el dashboard general como los 6
    /// dashboards de detalle por horno, sin volver a golpear la base de datos.
    /// </summary>
    Task<(List<RawMetricRow> Rows, string ShiftDesc)> FetchCurrentShiftRowsAsync(CancellationToken ct = default);

    /// <summary>
    /// Igual que arriba pero para un día pasado y un turno específico (1, 2 o 3) — usado por
    /// el histórico. No hay detección de "hora actual" aquí: se filtra directo por Shift_ID.
    /// </summary>
    Task<(List<RawMetricRow> Rows, string ShiftDesc)> FetchHistoricalRowsAsync(DateTime date, int shiftId, CancellationToken ct = default);

    /// <summary>Todas las filas de UN día completo (los 3 turnos, sin filtrar) — para las gráficas de OEE histórico.</summary>
    Task<List<RawMetricRow>> FetchDayRowsAsync(DateTime date, CancellationToken ct = default);

    /// <summary>Todas las filas de UNA semana completa (7 días × 3 turnos) en una sola llamada al SP
    /// (@Get_Weekly_Report=1) — mucho más barato que 7 llamadas diarias.</summary>
    Task<List<RawMetricRow>> FetchWeekRowsAsync(DateTime anyDateInWeek, CancellationToken ct = default);

    /// <summary>Todos los meses del AÑO de la fecha dada, en una sola llamada al SP (@Get_Monthly_Report=1).
    /// Forma de columnas distinta a las anteriores — ver MonthlyRawRow.</summary>
    Task<List<MonthlyRawRow>> FetchYearMonthlyRowsAsync(DateTime anyDateInYear, CancellationToken ct = default);
}

public class MetricsRawDataService : IMetricsRawDataService
{
    private readonly string _connectionString;
    private readonly int _plantListId;
    private readonly ILogger<MetricsRawDataService> _logger;

    // "Shift 1 06:00-15:20" -> captura 06:00 y 15:20
    private static readonly Regex ShiftTimeRegex = new(@"(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    public MetricsRawDataService(IConfiguration config, ILogger<MetricsRawDataService> logger)
    {
        _connectionString = config.GetConnectionString("M2SReportServices") ?? string.Empty;
        _plantListId = config.GetValue<int>("PlantMetrics:PlantListId", 1);
        _logger = logger;
    }

    public async Task<(List<RawMetricRow> Rows, string ShiftDesc)> FetchCurrentShiftRowsAsync(CancellationToken ct = default)
    {
        var all = await FetchDayRowsAsync(DateTime.Today, ct);
        var now = DateTime.Now.TimeOfDay;
        var filtered = all.Where(r => IsWithinShift(r.ShiftDesc, now)).ToList();
        var shiftDesc = filtered.FirstOrDefault()?.ShiftDesc ?? string.Empty;
        return (filtered, shiftDesc);
    }

    public async Task<(List<RawMetricRow> Rows, string ShiftDesc)> FetchHistoricalRowsAsync(DateTime date, int shiftId, CancellationToken ct = default)
    {
        var all = await FetchDayRowsAsync(date, ct);
        var filtered = all.Where(r => r.ShiftId == shiftId).ToList();
        var shiftDesc = filtered.FirstOrDefault()?.ShiftDesc ?? string.Empty;
        return (filtered, shiftDesc);
    }

    public async Task<List<RawMetricRow>> FetchDayRowsAsync(DateTime date, CancellationToken ct = default)
        => await ExecuteDayShapedReportAsync(date.Date, weekly: false, ct);

    public async Task<List<RawMetricRow>> FetchWeekRowsAsync(DateTime anyDateInWeek, CancellationToken ct = default)
        => await ExecuteDayShapedReportAsync(anyDateInWeek.Date, weekly: true, ct);

    /// <summary>@Get_Daily_Report=1 y @Get_Weekly_Report=1 regresan la MISMA forma de columnas
    /// (Report_Group 1/2/3, Hour_by_Hour, Shift_ID, etc.) — la semanal solo trae 7 días de esas
    /// filas en vez de 1. Por eso comparten el mismo lector.</summary>
    private async Task<List<RawMetricRow>> ExecuteDayShapedReportAsync(DateTime date, bool weekly, CancellationToken ct)
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
        cmd.Parameters.AddWithValue("@Plant_List_ID", _plantListId);
        cmd.Parameters.AddWithValue("@Get_Daily_Report", !weekly);
        cmd.Parameters.AddWithValue("@Get_Weekly_Report", weekly);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        int ordReportGroup = reader.GetOrdinal("Report_Group");
        int ordGroupId = reader.GetOrdinal("Product_Group_ID");
        int ordDesc = reader.GetOrdinal("Product_Desc");
        int ordProductOrder = reader.GetOrdinal("Product_Order");
        int ordProductListId = reader.GetOrdinal("Product_List_ID");
        int ordCycleTime = reader.GetOrdinal("Cycle_Time_Secs");
        int ordPlannedForOee = reader.GetOrdinal("Planned_Shift_for_OEE");
        int ordAccumRate = reader.GetOrdinal("Accumulated_Rate");
        int ordOeeShift = reader.GetOrdinal("OEE_Shift");
        int ordTotal = reader.GetOrdinal("Total");
        int ordTotalSap = reader.GetOrdinal("Total_SAP");
        int ordHour = reader.GetOrdinal("Hour_by_Hour");
        int ordShiftId = reader.GetOrdinal("Shift_ID");
        int ordShiftDesc = reader.GetOrdinal("Shift_Desc");
        int ordEventDateShort = TryGetOrdinal(reader, "Event_Date_Short");

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new RawMetricRow(
                ReportGroup: SafeGetInt(reader, ordReportGroup, -1),
                GroupId: SafeGetInt(reader, ordGroupId, -1),
                Desc: SafeGetString(reader, ordDesc),
                ProductOrder: SafeGetInt(reader, ordProductOrder),
                ProductListId: SafeGetInt(reader, ordProductListId),
                CycleTimeSecs: SafeGetDouble(reader, ordCycleTime),
                PlannedForOee: SafeGetInt(reader, ordPlannedForOee),
                AccumRate: SafeGetInt(reader, ordAccumRate),
                OeeShift: SafeGetDouble(reader, ordOeeShift),
                Total: SafeGetInt(reader, ordTotal),
                TotalSap: SafeGetInt(reader, ordTotalSap),
                Hour: SafeGetString(reader, ordHour),
                ShiftId: SafeGetInt(reader, ordShiftId, -1),
                ShiftDesc: SafeGetString(reader, ordShiftDesc),
                EventDateShort: ordEventDateShort >= 0 ? SafeGetString(reader, ordEventDateShort) : string.Empty
            ));
        }

        return rows;
    }

    /// <summary>
    /// @Get_Monthly_Report=1: UN llamado regresa TODOS los meses del año de @Start_Date
    /// (el SP genera el año completo internamente y agrega por mes). Columnas distintas al
    /// modo diario/semanal — se leen por nombre y de forma defensiva; si algún nombre no
    /// coincide con el real en tu SP, se loguea y esa columna regresa vacía en vez de tronar.
    /// </summary>
    public async Task<List<MonthlyRawRow>> FetchYearMonthlyRowsAsync(DateTime anyDateInYear, CancellationToken ct = default)
    {
        var rows = new List<MonthlyRawRow>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("[dbo].[Plant_Metrics_Production_Reports]", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 60
        };

        cmd.Parameters.AddWithValue("@Start_Date", anyDateInYear.Date);
        cmd.Parameters.AddWithValue("@Plant_List_ID", _plantListId);
        cmd.Parameters.AddWithValue("@Get_Monthly_Report", true);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        int ordReportGroup = TryGetOrdinal(reader, "Report_Group");
        int ordGroupId = TryGetOrdinal(reader, "Product_Group_ID");
        int ordDesc = TryGetOrdinal(reader, "Product_Desc");
        int ordPlannedShift = TryGetOrdinal(reader, "Planned_Shift");
        int ordMonthName = TryGetOrdinal(reader, "Event_Date_Short_Month_Name");
        int ordMonthNumber = TryGetOrdinal(reader, "Event_Date_Short_Month_Number");
        int ordAccumRate = TryGetOrdinal(reader, "Accumulated_Rate");
        int ordTotal = TryGetOrdinal(reader, "Total");
        int ordTotalSap = TryGetOrdinal(reader, "Total_SAP");
        int ordPlannedForOee = TryGetOrdinal(reader, "Planned_for_OEE");
        int ordOee = TryGetOrdinal(reader, "OEE");

        if (ordReportGroup < 0 || ordMonthNumber < 0 || ordTotal < 0)
        {
            _logger.LogError("FetchYearMonthlyRowsAsync: no encontré las columnas esperadas del modo @Get_Monthly_Report=1 — revisa los nombres de columna contra tu SP real.");
            return rows;
        }

        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MonthlyRawRow(
                ReportGroup: SafeGetInt(reader, ordReportGroup, -1),
                GroupId: ordGroupId >= 0 ? SafeGetInt(reader, ordGroupId, -1) : -1,
                Desc: ordDesc >= 0 ? SafeGetString(reader, ordDesc) : string.Empty,
                PlannedShift: ordPlannedShift >= 0 ? SafeGetInt(reader, ordPlannedShift) : 0,
                MonthName: ordMonthName >= 0 ? SafeGetString(reader, ordMonthName) : string.Empty,
                MonthNumber: SafeGetInt(reader, ordMonthNumber),
                AccumulatedRate: ordAccumRate >= 0 ? SafeGetInt(reader, ordAccumRate) : 0,
                Total: SafeGetInt(reader, ordTotal),
                TotalSap: ordTotalSap >= 0 ? SafeGetInt(reader, ordTotalSap) : 0,
                PlannedForOee: ordPlannedForOee >= 0 ? SafeGetInt(reader, ordPlannedForOee) : 0,
                Oee: ordOee >= 0 ? SafeGetDouble(reader, ordOee) : 0
            ));
        }

        return rows;
    }

    private static int TryGetOrdinal(SqlDataReader reader, string columnName)
    {
        try { return reader.GetOrdinal(columnName); }
        catch (IndexOutOfRangeException) { return -1; }
    }

    // ------------------------------------------------------------------
    // Lecturas defensivas: este SP arma su resultado con varios UNION ALL y columnas
    // calculadas con CASE/dynamic SQL, así que NO asumimos el tipo exacto que .NET cree
    // que debería tener cada columna. Se toma el valor crudo (GetValue) y se convierte
    // de forma segura sin importar si llega como int, decimal, double o incluso string.
    // ------------------------------------------------------------------
    private static int SafeGetInt(SqlDataReader reader, int ordinal, int fallback = 0)
    {
        if (ordinal < 0 || reader.IsDBNull(ordinal)) return fallback;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            int i => i,
            short s => s,
            byte b => b,
            long l => (int)l,
            decimal d => (int)d,
            double db => (int)db,
            float f => (int)f,
            string str => int.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback,
            _ => TryConvert(() => Convert.ToInt32(value, CultureInfo.InvariantCulture), fallback)
        };
    }

    private static double SafeGetDouble(SqlDataReader reader, int ordinal, double fallback = 0)
    {
        if (ordinal < 0 || reader.IsDBNull(ordinal)) return fallback;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            double db => db,
            float f => f,
            decimal d => (double)d,
            int i => i,
            short s => s,
            byte b => b,
            long l => l,
            string str => double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback,
            _ => TryConvert(() => Convert.ToDouble(value, CultureInfo.InvariantCulture), fallback)
        };
    }

    private static string SafeGetString(SqlDataReader reader, int ordinal)
    {
        if (ordinal < 0 || reader.IsDBNull(ordinal)) return string.Empty;
        var value = reader.GetValue(ordinal);
        return value?.ToString()?.Trim() ?? string.Empty;
    }

    private static T TryConvert<T>(Func<T> convert, T fallback)
    {
        try { return convert(); } catch { return fallback; }
    }

    /// <summary>
    /// ¿La hora actual cae dentro del rango que trae este Shift_Desc? (ej. "Shift 1 06:00-15:20").
    /// Soporta turnos que cruzan medianoche. Nada hardcodeado: el rango sale del propio SP.
    /// </summary>
    private static bool IsWithinShift(string shiftDesc, TimeSpan now)
    {
        var m = ShiftTimeRegex.Match(shiftDesc ?? string.Empty);
        if (!m.Success) return false;

        var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        var end = new TimeSpan(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0);

        return end <= start
            ? (now >= start || now < end)
            : (now >= start && now < end);
    }
}
