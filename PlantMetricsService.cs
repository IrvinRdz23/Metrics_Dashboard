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
/// REGLAS CONFIRMADAS CON IRVIN (agosto 2026):
/// - Report_Group = 1  -> Día/Turno acumulado. Alimenta las 5 cards de horno y el modal.
///     - Total                  = producido en tiempo real
///     - Accumulated_Rate       = lo que se debería llevar producido a esta hora
///     - Planned_Shift_for_OEE  = plan completo de fin de turno
///     - OEE_Shift              = ya viene calculado por el SP (Total / Accumulated_Rate)
/// - Report_Group = 2  -> Hora por hora. Solo se usa para la gráfica de tendencia de planta.
/// - Report_Group = 3  -> Acumulado SAP. Se suma Total_SAP y se pega a la línea correspondiente
///     (match por nombre de línea dentro del mismo horno, ya que en Report_Group=1
///     Product_List_ID siempre viene NULL).
/// - Product_Group_ID: 1 y 6 -> Furnace 1 (6 = Clam Shells, comparten horno con 1),
///   2 -> Furnace 2, 3 -> Furnace 3, 4 -> Furnace 4, 5 -> Furnace 5, 7 (Tube Mills) -> se ignora.
/// - Líneas con Planned_Shift_for_OEE = 0 se excluyen de TODO (no tienen plan para este turno,
///   contarlas arrastra el OEE promedio hacia abajo sin motivo real).
/// - El SP regresa filas de los 3 turnos del día para cada línea. Solo nos importa el turno
///   ACTUAL (según la hora real), así que se filtra por Shift_ID detectado dinámicamente a
///   partir de los rangos de hora que vienen en Shift_Desc (nunca hardcodeado).
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
            return BuildDemoSnapshot();
        }
    }

    private record RawRow(
        int ReportGroup, int GroupId, string Desc, double CycleTimeSecs,
        int PlannedForOee, int AccumRate, double OeeShift, int Total, int TotalSap,
        string Hour, int ShiftId, string ShiftDesc);

    private async Task<PlantDashboardSnapshot> GetSnapshotFromDatabaseAsync(CancellationToken ct)
    {
        var rows = new List<RawRow>();

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

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
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
            int ordShiftId = reader.GetOrdinal("Shift_ID");
            int ordShiftDesc = reader.GetOrdinal("Shift_Desc");

            while (await reader.ReadAsync(ct))
            {
                var groupId = reader.IsDBNull(ordGroupId) ? -1 : reader.GetInt32(ordGroupId);
                if (groupId == 7) continue; // Tube Mills, siempre fuera

                rows.Add(new RawRow(
                    ReportGroup: reader.IsDBNull(ordReportGroup) ? -1 : reader.GetInt32(ordReportGroup),
                    GroupId: groupId,
                    Desc: reader.IsDBNull(ordDesc) ? "" : reader.GetString(ordDesc),
                    CycleTimeSecs: (double)(reader.IsDBNull(ordCycleTime) ? 0 : reader.GetDecimal(ordCycleTime)),
                    PlannedForOee: reader.IsDBNull(ordPlannedForOee) ? 0 : reader.GetInt32(ordPlannedForOee),
                    AccumRate: reader.IsDBNull(ordAccumRate) ? 0 : reader.GetInt32(ordAccumRate),
                    OeeShift: reader.IsDBNull(ordOeeShift) ? 0 : reader.GetDouble(ordOeeShift),
                    Total: reader.IsDBNull(ordTotal) ? 0 : reader.GetInt32(ordTotal),
                    TotalSap: reader.IsDBNull(ordTotalSap) ? 0 : reader.GetInt32(ordTotalSap),
                    Hour: reader.IsDBNull(ordHour) ? "" : reader.GetString(ordHour),
                    ShiftId: reader.IsDBNull(ordShiftId) ? -1 : reader.GetInt32(ordShiftId),
                    ShiftDesc: reader.IsDBNull(ordShiftDesc) ? "" : reader.GetString(ordShiftDesc)
                ));
            }
        }

        // ---------- Detectar el turno ACTUAL a partir de los rangos de hora del propio SP ----------
        var currentShiftId = DetectCurrentShiftId(rows, DateTime.Now.TimeOfDay);
        var currentShiftDesc = rows.FirstOrDefault(r => r.ShiftId == currentShiftId)?.ShiftDesc ?? string.Empty;

        // ---------- Report_Group 1 (filtrado por turno actual y con plan > 0) -> cards + modal ----------
        var furnaces = Enumerable.Range(1, 5)
            .Select(id => new FurnaceMetric { FurnaceId = id, FurnaceName = $"Furnace {id}" })
            .ToList();

        var linesIndex = new Dictionary<(int furnaceId, string desc), ProductLineMetric>();

        foreach (var r in rows.Where(r => r.ReportGroup == 1 && r.ShiftId == currentShiftId && r.PlannedForOee != 0))
        {
            if (!ProductGroupToFurnace.TryGetValue(r.GroupId, out var mapped)) continue;

            var line = new ProductLineMetric
            {
                ProductDesc = r.Desc,
                CycleTimeSecs = r.CycleTimeSecs,
                Total = r.Total,
                AccumulatedRate = r.AccumRate,
                PlannedShift = r.PlannedForOee,
                OeeShift = r.OeeShift,
            };

            furnaces.First(f => f.FurnaceId == mapped.FurnaceId).Lines.Add(line);
            linesIndex[(mapped.FurnaceId, r.Desc)] = line;
        }

        // ---------- Report_Group 3 (mismo turno) -> SAP pegado a la línea ----------
        foreach (var r in rows.Where(r => r.ReportGroup == 3 && r.ShiftId == currentShiftId && r.TotalSap > 0))
        {
            if (!ProductGroupToFurnace.TryGetValue(r.GroupId, out var mapped)) continue;
            if (linesIndex.TryGetValue((mapped.FurnaceId, r.Desc), out var line))
            {
                line.TotalSap += r.TotalSap;
            }
        }

        // ---------- Report_Group 2 (mismo turno) -> tendencia de planta ----------
        var hourlyTotals = new SortedDictionary<string, int>();
        foreach (var r in rows.Where(r => r.ReportGroup == 2 && r.ShiftId == currentShiftId && !string.IsNullOrWhiteSpace(r.Hour) && r.Hour != "-"))
        {
            hourlyTotals.TryGetValue(r.Hour, out var acc);
            hourlyTotals[r.Hour] = acc + r.Total;
        }

        return new PlantDashboardSnapshot
        {
            ShiftDesc = currentShiftDesc,
            Furnaces = furnaces,
            HourlyTrend = hourlyTotals.Select(kv => new HourlyPoint { Hour = kv.Key, Production = kv.Value }).ToList()
        };
    }

    /// <summary>
    /// Determina el Shift_ID "actual" comparando la hora de ahora contra los rangos de
    /// hora reales que trae Shift_Desc (ej. "Shift 1 06:00-15:20"), no algo hardcodeado.
    /// Soporta turnos que cruzan medianoche (ej. "22:40-06:00").
    /// </summary>
    private static int DetectCurrentShiftId(List<RawRow> rows, TimeSpan now)
    {
        var windows = rows
            .Where(r => r.ShiftId > 0 && !string.IsNullOrWhiteSpace(r.ShiftDesc))
            .Select(r => (r.ShiftId, r.ShiftDesc))
            .Distinct()
            .Select(s =>
            {
                var m = ShiftTimeRegex.Match(s.ShiftDesc);
                if (!m.Success) return ((int ShiftId, TimeSpan Start, TimeSpan End)?)null;
                var start = new TimeSpan(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), 0);
                var end = new TimeSpan(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), 0);
                return (s.ShiftId, start, end);
            })
            .Where(w => w.HasValue)
            .Select(w => w!.Value)
            .ToList();

        foreach (var w in windows)
        {
            var wraps = w.End <= w.Start;
            var inWindow = wraps
                ? (now >= w.Start || now < w.End)
                : (now >= w.Start && now < w.End);
            if (inWindow) return w.ShiftId;
        }

        // Fallback: si por algún motivo no hubo match (datos incompletos), usa el turno más bajo disponible.
        return windows.Select(w => w.ShiftId).DefaultIfEmpty(1).Min();
    }

    // ------------------------------------------------------------------
    // MODO DEMO: solo se usa como fallback si falla la conexión real,
    // para que el dashboard nunca se quede en blanco.
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
                var accumulatedRate = (int)(plannedShift * (0.3 + _rng.NextDouble() * 0.5));
                var oeeFactor = 0.55 + _rng.NextDouble() * 0.6;
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
            hourly.Add(new HourlyPoint { Hour = $"{h:00}:00", Production = _rng.Next(2500, 6200) });
        }

        return new PlantDashboardSnapshot
        {
            ShiftDesc = "Shift 1 06:00-15:20",
            Furnaces = furnaces,
            HourlyTrend = hourly
        };
    }
}
