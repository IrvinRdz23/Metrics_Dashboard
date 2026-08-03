using Microsoft.Data.SqlClient;
using PlantMetricsDashboard.Models;

namespace PlantMetricsDashboard.Services;

public interface IPlantMetricsService
{
    Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

/// <summary>
/// Capa de datos ADO.NET puro (sin ORM) contra [dbo].[Plant_Metrics_Production_Reports].
///
/// NOTA IMPORTANTE PARA IRVIN:
/// El SP devuelve un único result set que mezcla Report_Group 1 (Día/Turno agregado),
/// 2 (Hora por hora) y 3 (SAP acumulado). Aquí se separan por Report_Group y se
/// agregan por Product_Group_Desc (= horno) y Product_Desc (= línea).
/// Ajusta el mapeo de columnas / agregación según las reglas exactas de negocio
/// que solo tú conoces (p. ej. si Planned_Shift_for_OEE debe usarse en vez de
/// Planned_Shift crudo). Está diseñado para que ese ajuste sea localizado aquí,
/// sin tocar Controllers/Views/SignalR.
/// </summary>
public class PlantMetricsService : IPlantMetricsService
{
    private readonly string _connectionString;
    private readonly int _plantListId;
    private readonly bool _useDemoData;
    private readonly ILogger<PlantMetricsService> _logger;

    // Nombres de horno tal como aparecen como encabezado de sección en el reporte por correo.
    private static readonly string[] FurnaceNames =
    {
        "Furnace 1", "Furnace 2", "Furnace 3", "Furnace 4", "Furnace 5"
    };

    public PlantMetricsService(IConfiguration config, ILogger<PlantMetricsService> logger)
    {
        _connectionString = config.GetConnectionString("M2SReportServices") ?? string.Empty;
        _plantListId = config.GetValue<int>("PlantMetrics:PlantListId", 1);
        _useDemoData = config.GetValue<bool>("PlantMetrics:UseDemoData", true);
        _logger = logger;
    }

    public async Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (_useDemoData)
        {
            return BuildDemoSnapshot();
        }

        try
        {
            return await GetSnapshotFromDatabaseAsync(ct);
        }
        catch (Exception ex)
        {
            // Nunca tumbar el dashboard por un error de datos: se loguea y se
            // regresa el último snapshot demo como fallback visual.
            _logger.LogError(ex, "Error obteniendo snapshot de Plant_Metrics_Production_Reports");
            return BuildDemoSnapshot();
        }
    }

    private async Task<PlantDashboardSnapshot> GetSnapshotFromDatabaseAsync(CancellationToken ct)
    {
        var furnaces = FurnaceNames
            .Select((name, idx) => new FurnaceMetric { FurnaceId = idx + 1, FurnaceName = name })
            .ToList();

        var hourlyTotals = new SortedDictionary<string, (int prod, int planned)>();

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
        cmd.Parameters.AddWithValue("@Get_Weekly_Report", false);
        cmd.Parameters.AddWithValue("@Get_Monthly_Report", false);
        cmd.Parameters.AddWithValue("@Is_Email_Request_Report", false);
        cmd.Parameters.AddWithValue("@Send_Email_Alerts", false);
        cmd.Parameters.AddWithValue("@Is_Test_Report", false);
        cmd.Parameters.AddWithValue("@Enable_Logs_and_Query_Results", false);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        int ordReportGroup = -1, ordGroupDesc = -1, ordDesc = -1, ordPlanned = -1,
            ordTotal = -1, ordTotalSap = -1, ordHour = -1, ordCycleTime = -1,
            ordCutMin = -1, ordProductListId = -1;

        bool ordinalsResolved = false;

        while (await reader.ReadAsync(ct))
        {
            if (!ordinalsResolved)
            {
                ordReportGroup = reader.GetOrdinal("Report_Group");
                ordGroupDesc = reader.GetOrdinal("Product_Group_Desc");
                ordDesc = reader.GetOrdinal("Product_Desc");
                ordPlanned = reader.GetOrdinal("Planned_Shift");
                ordTotal = reader.GetOrdinal("Total");
                ordTotalSap = reader.GetOrdinal("Total_SAP");
                ordHour = reader.GetOrdinal("Hour_by_Hour");
                ordCycleTime = reader.GetOrdinal("Cycle_Time_Secs");
                ordCutMin = reader.GetOrdinal("Cut_Min");
                ordProductListId = reader.GetOrdinal("Product_List_ID");
                ordinalsResolved = true;
            }

            var reportGroup = reader.IsDBNull(ordReportGroup) ? -1 : reader.GetInt32(ordReportGroup);
            var groupDesc = reader.IsDBNull(ordGroupDesc) ? "" : reader.GetString(ordGroupDesc).Trim();
            var furnace = furnaces.FirstOrDefault(f =>
                string.Equals(f.FurnaceName, groupDesc, StringComparison.OrdinalIgnoreCase));

            // Report_Group = 2 -> detalle hora por hora, se usa para líneas por horno y tendencia general
            if (reportGroup == 2 && furnace != null)
            {
                var productDesc = reader.IsDBNull(ordDesc) ? "" : reader.GetString(ordDesc);
                var planned = reader.IsDBNull(ordPlanned) ? 0 : reader.GetInt32(ordPlanned);
                var total = reader.IsDBNull(ordTotal) ? 0 : reader.GetInt32(ordTotal);
                var totalSap = reader.IsDBNull(ordTotalSap) ? 0 : reader.GetInt32(ordTotalSap);
                var hour = reader.IsDBNull(ordHour) ? "" : reader.GetString(ordHour);

                var line = furnace.Lines.FirstOrDefault(l => l.ProductDesc == productDesc);
                if (line == null)
                {
                    line = new ProductLineMetric
                    {
                        ProductListId = reader.IsDBNull(ordProductListId) ? 0 : reader.GetInt32(ordProductListId),
                        ProductDesc = productDesc,
                        CycleTimeSecs = reader.IsDBNull(ordCycleTime) ? 0 : reader.GetDouble(ordCycleTime),
                        CutMin = reader.IsDBNull(ordCutMin) ? 0 : reader.GetDouble(ordCutMin),
                    };
                    furnace.Lines.Add(line);
                }
                line.PlannedShift += planned;
                line.Total += total;
                line.TotalSap += totalSap;

                if (!string.IsNullOrWhiteSpace(hour))
                {
                    if (!hourlyTotals.TryGetValue(hour, out var acc)) acc = (0, 0);
                    hourlyTotals[hour] = (acc.prod + total, acc.planned + planned);
                }
            }
        }

        var snapshot = new PlantDashboardSnapshot
        {
            Furnaces = furnaces,
            HourlyTrend = hourlyTotals.Select(kv => new HourlyPoint
            {
                Hour = kv.Key,
                Production = kv.Value.prod,
                Planned = kv.Value.planned
            }).ToList()
        };

        return snapshot;
    }

    // ------------------------------------------------------------------
    // MODO DEMO: genera datos realistas para poder ver y probar el
    // dashboard completo (SignalR, cross-filter, light/dark, presentación)
    // sin necesidad de conexión a SQL Server. Pon "UseDemoData": false en
    // appsettings.json cuando conectes datos reales.
    // ------------------------------------------------------------------
    private static readonly Random _rng = new();

    private PlantDashboardSnapshot BuildDemoSnapshot()
    {
        var lineNamesByFurnace = new Dictionary<string, string[]>
        {
            ["Furnace 1"] = new[] { "PTC PCM CM1 (Line 2)", "PTC PCM Evaporator", "PTC Clam Shell 1", "PTC Clam Shell 2", "PTC Clam Shell 3", "PTC Clam Shell 4", "PTC Clam Shell 5" },
            ["Furnace 2"] = new[] { "PTC BMW LTR CB", "PTC BMW LTR", "PTC Tesla Y LTR CB", "PTC Tesla Y LTR CB 2", "PTC Tesla Y LTR" },
            ["Furnace 3"] = new[] { "PTC BMW HTR", "PTC BMW HTR CB", "PTC Toyota 24PL Radiator", "PTC Toyota 24PL Radiator CB", "PTC Toyota 24PL Radiator CB 2" },
            ["Furnace 4"] = new[] { "PTC Toyota ICAC CB", "PTC Toyota ICAC", "PTC GM LM2 ICAC CB", "PTC GM LM2 ICAC", "PTC Toyota ICAC CB 2" },
            ["Furnace 5"] = new[] { "PTC BMW Condenser CB", "PTC BMW Condenser", "PTC Honda TG7 Condenser CB", "PTC Honda T90 Condenser", "PTC RIVIAN LTR CB" },
        };

        var furnaces = new List<FurnaceMetric>();
        int fId = 1;
        foreach (var (furnaceName, lineNames) in lineNamesByFurnace)
        {
            var furnace = new FurnaceMetric { FurnaceId = fId++, FurnaceName = furnaceName };
            foreach (var lineName in lineNames)
            {
                var planned = _rng.Next(300, 900);
                var oeeFactor = 0.35 + _rng.NextDouble() * 0.6; // 35% - 95%
                var total = (int)(planned * oeeFactor);
                var sap = (int)(total * (0.7 + _rng.NextDouble() * 0.3));

                furnace.Lines.Add(new ProductLineMetric
                {
                    ProductDesc = lineName,
                    PlannedShift = planned,
                    Total = total,
                    TotalSap = sap,
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
                Production = _rng.Next(800, 1600),
                Planned = 1500
            });
        }

        return new PlantDashboardSnapshot
        {
            ShiftDesc = "Turno 1",
            Furnaces = furnaces,
            HourlyTrend = hourly
        };
    }
}
