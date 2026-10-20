using System.Data.SqlClient;
using Metrics_Dashboard.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Metrics_Dashboard.Services;

public interface IHeijunkaService
{
    /// <summary>
    /// Product_List_ID -> ¿tenía plan ese día/turno? Un ID que NO aparece en el diccionario significa
    /// "sin información de Heijunka" (apagado, sin datos esa semana, o línea sin grupo): quien llama
    /// cae al criterio de siempre (Planned_Shift_for_OEE &gt; 0).
    /// </summary>
    Task<Dictionary<int, bool>> IsPlannedBatchAsync(IEnumerable<int> productListIds, DateTime shiftDate, int shiftId, CancellationToken ct = default);
}

/// <summary>
/// Heijunka listo para cuando Planta 4 lo use. Con PlantMetrics:HeijunkaEnabled = false no toca la
/// base y todas las vistas se comportan como "sin datos de Heijunka". Con true aplica las mismas
/// reglas que Planta 1: Product_List_ID -> Product_List_Group_ID (tabla Product_List) ->
/// fila de la semana en Heijunka_Plan_List -> columna {Día}_Shift_{N}.
/// </summary>
public class HeijunkaService : IHeijunkaService
{
    private readonly string _connectionString;
    private readonly PlantOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HeijunkaService> _logger;

    public HeijunkaService(IConfiguration config, IOptions<PlantOptions> options, IMemoryCache cache, ILogger<HeijunkaService> logger)
    {
        _connectionString = config.GetConnectionString("M2SReportServices") ?? string.Empty;
        _options = options.Value;
        _cache = cache;
        _logger = logger;
    }

    public async Task<Dictionary<int, bool>> IsPlannedBatchAsync(IEnumerable<int> productListIds, DateTime shiftDate, int shiftId, CancellationToken ct = default)
    {
        var result = new Dictionary<int, bool>();
        if (!_options.HeijunkaEnabled) return result;

        var ids = productListIds.Where(i => i > 0).Distinct().ToList();
        if (ids.Count == 0 || shiftId <= 0) return result;

        if (_options.UseDemoData)
        {
            foreach (var id in ids) result[id] = id % 7 != 0;
            return result;
        }

        try
        {
            var groupOf = await GetProductListGroupsAsync(ct);
            var weekPlan = await GetWeekPlanAsync(shiftDate, shiftId, ct);

            foreach (var id in ids)
            {
                if (!groupOf.TryGetValue(id, out var listGroupId)) continue;
                if (!weekPlan.TryGetValue(listGroupId, out var plan)) continue; // sin fila esa semana -> criterio viejo
                if (!plan.Enabled) continue;                                    // Heijunka apagado para ese grupo
                result[id] = plan.Planned;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo consultar Heijunka; se usa el criterio de Planned_Shift_for_OEE.");
            result.Clear();
        }

        return result;
    }

    /// <summary>Product_List_ID -> Product_List_Group_ID, de esta planta. Casi nunca cambia: 10 min de caché.</summary>
    private async Task<Dictionary<int, int>> GetProductListGroupsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue("hj-product-groups", out Dictionary<int, int>? cached) && cached != null) return cached;

        var map = new Dictionary<int, int>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT Product_List_ID, Product_List_Group_ID FROM [dbo].[Product_List] WHERE Plant_List_ID = @plant", conn);
        cmd.Parameters.AddWithValue("@plant", _options.PlantListId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            map[Convert.ToInt32(reader.GetValue(0))] = Convert.ToInt32(reader.GetValue(1));
        }

        _cache.Set("hj-product-groups", map, TimeSpan.FromMinutes(10));
        return map;
    }

    /// <summary>Product_List_Group_ID -> (Heijunka_Enabled, ¿planeado en ese día/turno?) de la semana de shiftDate.</summary>
    private async Task<Dictionary<int, (bool Enabled, bool Planned)>> GetWeekPlanAsync(DateTime shiftDate, int shiftId, CancellationToken ct)
    {
        var monday = shiftDate.Date.AddDays(-(((int)shiftDate.DayOfWeek + 6) % 7));
        var dayName = shiftDate.DayOfWeek.ToString(); // "Monday".."Sunday" (invariante)

        // Sábado y domingo solo tienen turnos 1 y 2; lunes a viernes tienen 1, 2 y 3.
        var weekend = shiftDate.DayOfWeek == DayOfWeek.Saturday || shiftDate.DayOfWeek == DayOfWeek.Sunday;
        var columnExists = shiftId >= 1 && shiftId <= (weekend ? 2 : 3);
        var plannedExpr = columnExists ? $"CAST([{dayName}_Shift_{shiftId}] AS int)" : "CAST(0 AS int)";

        var key = $"hj-week|{monday:yyyyMMdd}|{dayName}|{shiftId}";
        if (_cache.TryGetValue(key, out Dictionary<int, (bool, bool)>? cached) && cached != null) return cached;

        var plan = new Dictionary<int, (bool Enabled, bool Planned)>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"SELECT Product_List_Group_ID, CAST(Heijunka_Enabled AS int), {plannedExpr} " +
            "FROM [dbo].[Heijunka_Plan_List] WHERE CAST(Start_Date AS date) = @monday", conn);
        cmd.Parameters.AddWithValue("@monday", monday);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0)) continue;
            var groupId = Convert.ToInt32(reader.GetValue(0));
            var enabled = !reader.IsDBNull(1) && Convert.ToInt32(reader.GetValue(1)) != 0;
            var planned = !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2)) != 0;
            plan[groupId] = (enabled, planned);
        }

        _cache.Set(key, plan, TimeSpan.FromSeconds(60));
        return plan;
    }
}
