using Metrics_Dashboard.Models;
using System.Data.SqlClient;
using System.Text.RegularExpressions;

namespace Metrics_Dashboard.Services;

public interface IPlantMetricsService
{
    Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

/// <summary>
/// Capa de datos ADO.NET puro (sin ORM) contra [dbo].[Plant_Metrics_Production_Reports].
///
/// MAPEO CONFIRMADO CON IRVIN (agosto 2026):
/// - Report_Group = 1  -> Día/Turno acumulado. Esto alimenta las 5 cards de horno y el modal.
///     - Total                  = producido en tiempo real
///     - Accumulated_Rate       = lo que se debería llevar producido a esta hora
///     - Planned_Shift_for_OEE  = plan completo de fin de turno
///     - OEE_Shift              = ya viene calculado por el SP (Total / Accumulated_Rate)
///     - Se ignoran las líneas con Planned_Shift_for_OEE = 0 (sin plan para este turno).
/// - Report_Group = 2  -> Hora por hora. Solo se usa para la gráfica de tendencia de planta.
/// - Report_Group = 3  -> Acumulado SAP. Se suma Total_SAP y se pega a la línea correspondiente
///     (match por nombre de línea dentro del mismo horno, ya que en Report_Group=1
///     Product_List_ID siempre viene NULL).
/// - Product_Group_ID: 1 y 6 -> Furnace 1 (6 = Clam Shells, comparten horno con 1),
///   2 -> Furnace 2, 3 -> Furnace 3, 4 -> Furnace 4, 5 -> Furnace 5, 7 (Tube Mills) -> se ignora.
/// - El SP trae los 3 turnos del día para cada línea. Cada fila ya incluye su propio
///   Shift_Desc con el rango de hora (ej. "Shift 1 06:00-15:20"), así que se compara esa
///   hora contra la hora actual, fila por fila, para quedarnos solo con el turno vigente
///   — sin hardcodear horarios de turno en ningún lado.
/// </summary>
public class PlantMetricsService : IPlantMetricsService
{
    private readonly string _connectionString;
    private readonly int _plantListId;
    private readonly ILogger<PlantMetricsService> _logger;

    private static readonly Dictionary<int, (int FurnaceId, string FurnaceName)> ProductGroupToFurnace = new()
    {
        [1] = (1, "Furnace 1"),
        [6] = (1, "Furnace 1"), // Clam Shells -> mismo horno que 1
        [2] = (2, "Furnace 2"),
        [3] = (3, "Furnace 3"),
        [4] = (4, "Furnace 4"),
        [5] = (5, "Furnace 5"),
        // 7 = Tube Mills -> excluido intencionalmente de todo el dashboard
    };

    // "Shift 1 06:00-15:20" -> captura 06:00 y 15:20
    private static readonly Regex ShiftTimeRegex = new(@"(\d{1,2}):(\d{2})\s*-\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    public PlantMetricsService(IConfiguration config, ILogger<PlantMetricsService> logger)
    {
        _connectionString = config.GetConnectionString("M2SReportServices") ?? string.Empty;
        _plantListId = config.GetValue<int>("PlantMetrics:PlantListId", 1);
        _logger = logger;
    }

    public async Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            return await GetSnapshotFromDatabaseAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo snapshot de Plant_Metrics_Production_Reports");
            // Sin demo: si falla la conexión/lectura real, regresamos un snapshot vacío
            // (las 5 cards se ven como "sin producción") en vez de tronar la página completa.
            return new PlantDashboardSnapshot
            {
                ShiftDesc = string.Empty,
                Furnaces = Enumerable.Range(1, 5)
                    .Select(id => new FurnaceMetric { FurnaceId = id, FurnaceName = $"Furnace {id}" })
                    .ToList(),
                HourlyTrend = new List<HourlyPoint>()
            };
        }
    }

    private async Task<PlantDashboardSnapshot> GetSnapshotFromDatabaseAsync(CancellationToken ct)
    {
        // Un horno por cada FurnaceId 1..5, en orden.
        var furnaces = Enumerable.Range(1, 5)
            .Select(id => new FurnaceMetric { FurnaceId = id, FurnaceName = $"Furnace {id}" })
            .ToList();

        // Índice (furnaceId, productDesc) -> línea, para poder pegarle el SAP (Report_Group=3) después.
        var linesIndex = new Dictionary<(int furnaceId, string desc), ProductLineMetric>();

        // Acumulado de producción por hora (Report_Group=2), toda la planta, sin Tube Mills.
        var hourlyTotals = new SortedDictionary<string, int>();

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
        int ordCycleTime = reader.GetOrdinal("Cycle_Time_Secs");
        int ordPlannedForOee = reader.GetOrdinal("Planned_Shift_for_OEE");
        int ordAccumRate = reader.GetOrdinal("Accumulated_Rate");
        int ordOeeShift = reader.GetOrdinal("OEE_Shift");
        int ordTotal = reader.GetOrdinal("Total");
        int ordTotalSap = reader.GetOrdinal("Total_SAP");
        int ordHour = reader.GetOrdinal("Hour_by_Hour");
        int ordShiftDesc = reader.GetOrdinal("Shift_Desc");

        while (await reader.ReadAsync(ct))
        {
            var reportGroup = reader.IsDBNull(ordReportGroup) ? -1 : reader.GetInt32(ordReportGroup);
            var groupId = reader.IsDBNull(ordGroupId) ? -1 : reader.GetInt32(ordGroupId);

            // Tube Mills (Product_Group_ID = 7) se ignora en todo el dashboard.
            if (groupId == 7) continue;

            var rowShiftDesc = reader.IsDBNull(ordShiftDesc) ? "" : reader.GetString(ordShiftDesc);

            // Cada fila trae su propio rango de hora en Shift_Desc; solo nos quedamos
            // con las filas cuyo rango incluye la hora actual (= el turno vigente).
            var isCurrentShift = IsWithinShift(rowShiftDesc, now);
            if (!isCurrentShift) continue;

            if (string.IsNullOrEmpty(shiftDesc)) shiftDesc = rowShiftDesc;

            // ---------- Report_Group 1: día/turno acumulado -> cards de horno + modal ----------
            if (reportGroup == 1 && ProductGroupToFurnace.TryGetValue(groupId, out var mapped))
            {
                var plannedForOee = reader.IsDBNull(ordPlannedForOee) ? 0 : reader.GetInt32(ordPlannedForOee);

                // Sin plan para este turno -> no se cuenta (afectaría el OEE promedio sin motivo real).
                if (plannedForOee == 0) continue;

                var furnace = furnaces.First(f => f.FurnaceId == mapped.FurnaceId);
                var desc = reader.IsDBNull(ordDesc) ? "" : reader.GetString(ordDesc);

                var line = new ProductLineMetric
                {
                    ProductDesc = desc,
                    CycleTimeSecs = (double)(reader.IsDBNull(ordCycleTime) ? 0 : reader.GetDecimal(ordCycleTime)),
                    Total = reader.IsDBNull(ordTotal) ? 0 : reader.GetInt32(ordTotal),
                    AccumulatedRate = reader.IsDBNull(ordAccumRate) ? 0 : reader.GetInt32(ordAccumRate),
                    PlannedShift = plannedForOee,
                    OeeShift = reader.IsDBNull(ordOeeShift) ? 0 : reader.GetDouble(ordOeeShift),
                };

                furnace.Lines.Add(line);
                linesIndex[(mapped.FurnaceId, desc)] = line;
            }
            // ---------- Report_Group 2: hora por hora -> solo tendencia de planta ----------
            else if (reportGroup == 2)
            {
                var hour = reader.IsDBNull(ordHour) ? "" : reader.GetString(ordHour);
                var total = reader.IsDBNull(ordTotal) ? 0 : reader.GetInt32(ordTotal);
                if (!string.IsNullOrWhiteSpace(hour) && hour != "-")
                {
                    hourlyTotals.TryGetValue(hour, out var acc);
                    hourlyTotals[hour] = acc + total;
                }
            }
            // ---------- Report_Group 3: SAP -> se pega a la línea ya cargada por Report_Group 1 ----------
            else if (reportGroup == 3 && ProductGroupToFurnace.TryGetValue(groupId, out var mappedSap))
            {
                var desc = reader.IsDBNull(ordDesc) ? "" : reader.GetString(ordDesc);
                var totalSap = reader.IsDBNull(ordTotalSap) ? 0 : reader.GetInt32(ordTotalSap);
                if (totalSap > 0 && linesIndex.TryGetValue((mappedSap.FurnaceId, desc), out var line))
                {
                    line.TotalSap += totalSap;
                }
            }
        }

        return new PlantDashboardSnapshot
        {
            ShiftDesc = shiftDesc,
            Furnaces = furnaces,
            HourlyTrend = hourlyTotals.Select(kv => new HourlyPoint
            {
                Hour = kv.Key,
                Production = kv.Value
            }).ToList()
        };
    }

    /// <summary>
    /// ¿La hora actual cae dentro del rango que trae este Shift_Desc?
    /// (ej. "Shift 1 06:00-15:20"). Soporta turnos que cruzan medianoche.
    /// Nada hardcodeado: el rango sale del texto que manda el propio SP.
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
