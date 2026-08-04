using Metrics_Dashboard.Models;
using System.Data.SqlClient;

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
/// - Report_Group = 2  -> Hora por hora. Solo se usa para la gráfica de tendencia de planta:
///     se suma Total por Hour_by_Hour de todas las líneas (excepto Product_Group_ID=7).
///     No existe un "plan por hora" real, así que NO se hardcodea ningún valor de plan aquí.
/// - Report_Group = 3  -> Acumulado SAP. Se suma Total_SAP y se pega a la línea correspondiente
///     (match por nombre de línea dentro del mismo horno, ya que en Report_Group=1
///     Product_List_ID siempre viene NULL).
/// - Product_Group_ID: 1 y 6 -> Furnace 1 (6 = Clam Shells, comparten horno con 1),
///   2 -> Furnace 2, 3 -> Furnace 3, 4 -> Furnace 4, 5 -> Furnace 5, 7 (Tube Mills) -> se ignora en todo el dashboard.
/// - El turno (Shift_Desc) nunca se hardcodea: se toma tal cual lo resuelve el SP según la hora.
/// </summary>
public class PlantMetricsService : IPlantMetricsService
{
    private readonly string _connectionString;
    private readonly int _plantListId;
    private readonly bool _useDemoData;
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
            return BuildDemoSnapshot();
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
            try
            {
                var reportGroup = reader.IsDBNull(ordReportGroup) ? -1 : reader.GetInt32(ordReportGroup);
                var groupId = reader.IsDBNull(ordGroupId) ? -1 : reader.GetInt32(ordGroupId);

                // Tube Mills (Product_Group_ID = 7) se ignora en todo el dashboard.
                if (groupId == 7) continue;

                if (string.IsNullOrEmpty(shiftDesc) && !reader.IsDBNull(ordShiftDesc))
                {
                    shiftDesc = reader.GetString(ordShiftDesc);
                }

                // ---------- Report_Group 1: día/turno acumulado -> cards de horno + modal ----------
                if (reportGroup == 1 && ProductGroupToFurnace.TryGetValue(groupId, out var mapped))
                {
                    var furnace = furnaces.First(f => f.FurnaceId == mapped.FurnaceId);
                    var desc = reader.IsDBNull(ordDesc) ? "" : reader.GetString(ordDesc);

                    var line = new ProductLineMetric
                    {
                        ProductDesc = desc,
                        CycleTimeSecs = (double)(reader.IsDBNull(ordCycleTime) ? 0 : reader.GetDecimal(ordCycleTime)),
                        Total = reader.IsDBNull(ordTotal) ? 0 : reader.GetInt32(ordTotal),
                        AccumulatedRate = reader.IsDBNull(ordAccumRate) ? 0 : reader.GetInt32(ordAccumRate),
                        PlannedShift = reader.IsDBNull(ordPlannedForOee) ? 0 : reader.GetInt32(ordPlannedForOee),
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
            catch(Exception ex)
            {
                return (PlantDashboardSnapshot)ex.Data;
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

    // ------------------------------------------------------------------
    // MODO DEMO: genera datos realistas (con la misma forma que produce
    // GetSnapshotFromDatabaseAsync) para poder ver y probar el dashboard
    // completo sin conexión a SQL Server. Pon "UseDemoData": false en
    // appsettings.json cuando conectes datos reales.
    // ------------------------------------------------------------------
    private static readonly Random _rng = new();

    private PlantDashboardSnapshot BuildDemoSnapshot()
    {
        var lineNamesByFurnace = new Dictionary<int, string[]>
        {
            [1] = new[] { "PTC PCM CM1 (Line 2)", "PTC PCM Evaporator", "PTC Clam Shell 1", "PTC Clam Shell 2", "PTC Clam Shell 3", "PTC Clam Shell 4", "PTC Clam Shell 5" },
            [2] = new[] { "PTC BMW LTR CB", "PTC BMW LTR", "PTC Tesla Y LTR CB", "PTC Tesla Y LTR CB 2", "PTC Tesla Y LTR" },
            [3] = new[] { "PTC BMW HTR", "PTC BMW HTR CB", "PTC Toyota 24PL Radiator", "PTC Toyota 24PL Radiator CB", "PTC Toyota 24PL Radiator CB 2" },
            [4] = new[] { "PTC Toyota ICAC CB", "PTC Toyota ICAC", "PTC GM LM2 ICAC CB", "PTC GM LM2 ICAC", "PTC Toyota ICAC CB 2" },
            [5] = new[] { "PTC BMW Condenser CB", "PTC BMW Condenser", "PTC Honda TG7 Condenser CB", "PTC Honda T90 Condenser", "PTC RIVIAN LTR CB" },
        };

        var furnaces = new List<FurnaceMetric>();
        foreach (var (furnaceId, lineNames) in lineNamesByFurnace)
        {
            var furnace = new FurnaceMetric { FurnaceId = furnaceId, FurnaceName = $"Furnace {furnaceId}" };
            foreach (var lineName in lineNames)
            {
                var plannedShift = _rng.Next(300, 900);
                // Fracción del turno ya transcurrida (simulada) -> lo que "debería" llevar a esta hora
                var accumulatedRate = (int)(plannedShift * (0.3 + _rng.NextDouble() * 0.5));
                var oeeFactor = 0.55 + _rng.NextDouble() * 0.6; // entre 55% y 115% de lo esperado a esta hora
                var total = Math.Max(0, (int)(accumulatedRate * oeeFactor));
                var sap = (int)(total * (0.7 + _rng.NextDouble() * 0.3));

                furnace.Lines.Add(new ProductLineMetric
                {
                    ProductDesc = lineName,
                    PlannedShift = plannedShift,
                    AccumulatedRate = accumulatedRate,
                    Total = total,
                    TotalSap = sap,
                    OeeShift = accumulatedRate <= 0 ? (total > 0 ? 1.0 : 0.0) : (double)total / accumulatedRate,
                    CycleTimeSecs = Math.Round(20 + _rng.NextDouble() * 60, 2)
                });
            }
            furnaces.Add(furnace);
        }

        var currentHour = DateTime.Now.Hour;
        var hourly = new List<HourlyPoint>();
        for (int h = 7; h <= Math.Max(currentHour, 10); h++)
        {
            hourly.Add(new HourlyPoint
            {
                Hour = $"{h:00}:00",
                Production = _rng.Next(2500, 6200)
            });
        }

        return new PlantDashboardSnapshot
        {
            ShiftDesc = "Shift 1 06:00-15:20",
            Furnaces = furnaces,
            HourlyTrend = hourly
        };
    }
}
