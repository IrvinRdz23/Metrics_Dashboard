using Metrics_Dashboard.Models;
using System.Data.SqlClient;
using System.Text.RegularExpressions;

namespace Metrics_Dashboard.Services;

public interface IMetricsRawDataService
{
    /// <summary>
    /// Ejecuta [dbo].[Plant_Metrics_Production_Reports] UNA vez y regresa todas las filas
    /// del turno vigente (detectado por hora real contra el rango en Shift_Desc), de
    /// TODOS los Product_Group_ID (incluyendo el 7 / Tube Mills — cada consumidor decide
    /// si lo usa o lo excluye). De aquí se derivan tanto el dashboard general como los 6
    /// dashboards de detalle por horno, sin volver a golpear la base de datos.
    /// </summary>
    Task<(List<RawMetricRow> Rows, string ShiftDesc)> FetchCurrentShiftRowsAsync(CancellationToken ct = default);
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
        var rows = new List<RawMetricRow>();
        string shiftDesc = string.Empty;
        var now = DateTime.Now.TimeOfDay;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand("[dbo].[Plant_Metrics_Production_Reports]", conn)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 30
        };

        cmd.Parameters.AddWithValue("@Start_Date", DateTime.Today);
        cmd.Parameters.AddWithValue("@Plant_List_ID", _plantListId);
        cmd.Parameters.AddWithValue("@Get_Daily_Report", true);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        int ordReportGroup = reader.GetOrdinal("Report_Group");
        int ordGroupId = reader.GetOrdinal("Product_Group_ID");
        int ordDesc = reader.GetOrdinal("Product_Desc");
        int ordProductOrder = reader.GetOrdinal("Product_Order");
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
            var rowShiftDesc = reader.IsDBNull(ordShiftDesc) ? "" : reader.GetString(ordShiftDesc);
            if (!IsWithinShift(rowShiftDesc, now)) continue;

            if (string.IsNullOrEmpty(shiftDesc)) shiftDesc = rowShiftDesc;

            rows.Add(new RawMetricRow(
                ReportGroup: reader.IsDBNull(ordReportGroup) ? -1 : reader.GetInt32(ordReportGroup),
                GroupId: reader.IsDBNull(ordGroupId) ? -1 : reader.GetInt32(ordGroupId),
                Desc: reader.IsDBNull(ordDesc) ? "" : reader.GetString(ordDesc),
                ProductOrder: reader.IsDBNull(ordProductOrder) ? 0 : reader.GetInt32(ordProductOrder),
                CycleTimeSecs: (double)(reader.IsDBNull(ordCycleTime) ? 0 : reader.GetDecimal(ordCycleTime)),
                PlannedForOee: reader.IsDBNull(ordPlannedForOee) ? 0 : reader.GetInt32(ordPlannedForOee),
                AccumRate: reader.IsDBNull(ordAccumRate) ? 0 : reader.GetInt32(ordAccumRate),
                OeeShift: reader.IsDBNull(ordOeeShift) ? 0 : reader.GetDouble(ordOeeShift),
                Total: reader.IsDBNull(ordTotal) ? 0 : reader.GetInt32(ordTotal),
                TotalSap: reader.IsDBNull(ordTotalSap) ? 0 : reader.GetInt32(ordTotalSap),
                Hour: reader.IsDBNull(ordHour) ? "" : reader.GetString(ordHour),
                ShiftId: reader.IsDBNull(ordShiftId) ? -1 : reader.GetInt32(ordShiftId),
                ShiftDesc: rowShiftDesc
            ));
        }

        return (rows, shiftDesc);
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
            ? (now >= start || now < end)   // turno cruza medianoche
            : (now >= start && now < end);
    }
}
