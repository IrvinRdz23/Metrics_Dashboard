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
}

public class MetricsRawDataService : IMetricsRawDataService
{
    private readonly string _connectionString;
    private readonly int _plantListId;

    // "Shift 1 06:00-15:20" -> captura 06:00 y 15:20
    private static readonly Regex ShiftTimeRegex = new(@"(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    public MetricsRawDataService(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("M2SReportServices") ?? string.Empty;
        _plantListId = config.GetValue<int>("PlantMetrics:PlantListId", 1);
    }

    public async Task<(List<RawMetricRow> Rows, string ShiftDesc)> FetchCurrentShiftRowsAsync(CancellationToken ct = default)
    {
        var all = await ExecuteAndReadAllRowsAsync(DateTime.Today, ct);
        var now = DateTime.Now.TimeOfDay;
        var filtered = all.Where(r => IsWithinShift(r.ShiftDesc, now)).ToList();
        var shiftDesc = filtered.FirstOrDefault()?.ShiftDesc ?? string.Empty;
        return (filtered, shiftDesc);
    }

    public async Task<(List<RawMetricRow> Rows, string ShiftDesc)> FetchHistoricalRowsAsync(DateTime date, int shiftId, CancellationToken ct = default)
    {
        var all = await ExecuteAndReadAllRowsAsync(date.Date, ct);
        var filtered = all.Where(r => r.ShiftId == shiftId).ToList();
        var shiftDesc = filtered.FirstOrDefault()?.ShiftDesc ?? string.Empty;
        return (filtered, shiftDesc);
    }

    /// <summary>Ejecuta el SP para una fecha dada y regresa TODAS las filas (los 3 turnos,
    /// sin filtrar todavía) — cada método público de arriba decide cómo filtrarlas.</summary>
    private async Task<List<RawMetricRow>> ExecuteAndReadAllRowsAsync(DateTime date, CancellationToken ct)
    {
        var rows = new List<RawMetricRow>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("[dbo].[Plant_Metrics_Production_Reports]", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };

        cmd.Parameters.AddWithValue("@Start_Date", date);
        cmd.Parameters.AddWithValue("@Plant_List_ID", _plantListId);
        cmd.Parameters.AddWithValue("@Get_Daily_Report", true);

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
                ShiftDesc: SafeGetString(reader, ordShiftDesc)
            ));
        }

        return rows;
    }

    // ------------------------------------------------------------------
    // Lecturas defensivas: este SP arma su resultado con varios UNION ALL y columnas
    // calculadas con CASE/dynamic SQL, así que NO asumimos el tipo exacto que .NET cree
    // que debería tener cada columna. Se toma el valor crudo (GetValue) y se convierte
    // de forma segura sin importar si llega como int, decimal, double o incluso string.
    // ------------------------------------------------------------------
    private static int SafeGetInt(SqlDataReader reader, int ordinal, int fallback = 0)
    {
        if (reader.IsDBNull(ordinal)) return fallback;
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
        if (reader.IsDBNull(ordinal)) return fallback;
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
        if (reader.IsDBNull(ordinal)) return string.Empty;
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
    /// Solo se usa para el modo EN VIVO — el histórico filtra directo por Shift_ID.
    /// </summary>
    private static bool IsWithinShift(string shiftDesc, TimeSpan now)
    {
        var m = ShiftTimeRegex.Match(shiftDesc ?? string.Empty);
        if (!m.Success) return false;

        var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
        var end = new TimeSpan(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0);

        return end <= start
            ? (now >= start || now < end)   // turno cruza medianoche
            : (now >= start && now < end);
    }
}
