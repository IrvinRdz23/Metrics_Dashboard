using Metrics_Dashboard.Models;
using System.Data.SqlClient;

namespace Metrics_Dashboard.Services;

public interface IHeijunkaService
{
    /// <summary>
    /// true = Heijunka dice que esta línea (Product_List_ID) tenía plan ese día/turno.
    /// false = Heijunka dice que NO tenía plan.
    /// null = no hay datos de Heijunka para esa semana todavía, o la línea no se pudo mapear
    /// a un Product_List_Group_ID — en ese caso, el que llama debe usar el criterio viejo
    /// (Planned_Shift_for_OEE != 0) como respaldo, tal como pidió Irvin.
    /// </summary>
    Task<Dictionary<int, bool?>> IsPlannedBatchAsync(IEnumerable<int> productListIds, DateTime date, int shiftId, CancellationToken ct = default);
}

/// <summary>
/// Cruce completo: Product_List_ID (SP) -> Product_List_Group_ID (tabla Product_List) ->
/// bandera del día/turno (Heijunka_Plan_List, Start_Date = lunes de la semana vigente).
///
/// Ambos catálogos se cachean en memoria (Singleton) porque son datos que casi no cambian
/// día a día — Product_List es configuración de líneas, y Heijunka_Plan_List se actualiza
/// por semana, no por request. Se refrescan solos cada cierto tiempo, y el plan Heijunka
/// además se recarga automáticamente si cambia la semana.
/// </summary>
public class HeijunkaService : IHeijunkaService
{
    private readonly string _connectionString;
    private readonly ILogger<HeijunkaService> _logger;

    private static readonly TimeSpan MappingRefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PlanRefreshInterval = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _mappingLock = new(1, 1);
    private readonly SemaphoreSlim _planLock = new(1, 1);

    private Dictionary<int, int> _productListToGroup = new(); // Product_List_ID -> Product_List_Group_ID
    private DateTime _mappingLoadedAt = DateTime.MinValue;

    private Dictionary<int, HeijunkaGroupPlan> _plansByGroup = new(); // Product_List_Group_ID -> plan de la semana vigente
    private DateTime _plansLoadedAt = DateTime.MinValue;
    private DateTime _plansForMonday = DateTime.MinValue;
    private bool _hasDataForCurrentWeek;

    public HeijunkaService(IConfiguration config, ILogger<HeijunkaService> logger)
    {
        _connectionString = config.GetConnectionString("M2SReportServices") ?? string.Empty;
        _logger = logger;
    }

    public async Task<Dictionary<int, bool?>> IsPlannedBatchAsync(IEnumerable<int> productListIds, DateTime date, int shiftId, CancellationToken ct = default)
    {
        await EnsureMappingLoadedAsync(ct);
        await EnsurePlansLoadedAsync(date, ct);

        var result = new Dictionary<int, bool?>();
        var day = date.DayOfWeek;

        foreach (var id in productListIds.Distinct())
        {
            if (!_hasDataForCurrentWeek) { result[id] = null; continue; }
            if (id <= 0 || !_productListToGroup.TryGetValue(id, out var groupId)) { result[id] = null; continue; }
            if (!_plansByGroup.TryGetValue(groupId, out var plan)) { result[id] = null; continue; }

            result[id] = plan.IsPlanned(day, shiftId);
        }

        return result;
    }

    private async Task EnsureMappingLoadedAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _mappingLoadedAt < MappingRefreshInterval && _productListToGroup.Count > 0) return;

        await _mappingLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _mappingLoadedAt < MappingRefreshInterval && _productListToGroup.Count > 0) return;

            var map = new Dictionary<int, int>();
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT Product_List_ID, Product_List_Group_ID FROM dbo.Product_List", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(1)) continue;
                map[reader.GetInt32(0)] = reader.GetInt32(1);
            }

            _productListToGroup = map;
            _mappingLoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cargando el mapeo Product_List -> Product_List_Group_ID para Heijunka");
        }
        finally
        {
            _mappingLock.Release();
        }
    }

    private async Task EnsurePlansLoadedAsync(DateTime date, CancellationToken ct)
    {
        var monday = date.AddDays(-(int)((7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7)).Date;

        if (monday == _plansForMonday && DateTime.UtcNow - _plansLoadedAt < PlanRefreshInterval) return;

        await _planLock.WaitAsync(ct);
        try
        {
            if (monday == _plansForMonday && DateTime.UtcNow - _plansLoadedAt < PlanRefreshInterval) return;

            var plans = new Dictionary<int, HeijunkaGroupPlan>();
            var found = false;

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
                SELECT Product_List_Group_ID,
                       Monday_Shift_1, Monday_Shift_2, Monday_Shift_3,
                       Tuesday_Shift_1, Tuesday_Shift_2, Tuesday_Shift_3,
                       Wednesday_Shift_1, Wednesday_Shift_2, Wednesday_Shift_3,
                       Thursday_Shift_1, Thursday_Shift_2, Thursday_Shift_3,
                       Friday_Shift_1, Friday_Shift_2, Friday_Shift_3,
                       Saturday_Shift_1, Saturday_Shift_2,
                       Sunday_Shift_1, Sunday_Shift_2
                FROM dbo.Heijunka_Plan_List
                WHERE Start_Date = @Monday", conn);
            cmd.Parameters.AddWithValue("@Monday", monday);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                found = true;
                var groupId = reader.GetInt32(0);
                var plan = new HeijunkaGroupPlan { ProductListGroupId = groupId };

                bool G(int i) => !reader.IsDBNull(i) && Convert.ToBoolean(reader.GetValue(i));

                plan.SetFlag(DayOfWeek.Monday, 1, G(1));
                plan.SetFlag(DayOfWeek.Monday, 2, G(2));
                plan.SetFlag(DayOfWeek.Monday, 3, G(3));
                plan.SetFlag(DayOfWeek.Tuesday, 1, G(4));
                plan.SetFlag(DayOfWeek.Tuesday, 2, G(5));
                plan.SetFlag(DayOfWeek.Tuesday, 3, G(6));
                plan.SetFlag(DayOfWeek.Wednesday, 1, G(7));
                plan.SetFlag(DayOfWeek.Wednesday, 2, G(8));
                plan.SetFlag(DayOfWeek.Wednesday, 3, G(9));
                plan.SetFlag(DayOfWeek.Thursday, 1, G(10));
                plan.SetFlag(DayOfWeek.Thursday, 2, G(11));
                plan.SetFlag(DayOfWeek.Thursday, 3, G(12));
                plan.SetFlag(DayOfWeek.Friday, 1, G(13));
                plan.SetFlag(DayOfWeek.Friday, 2, G(14));
                plan.SetFlag(DayOfWeek.Friday, 3, G(15));
                plan.SetFlag(DayOfWeek.Saturday, 1, G(16));
                plan.SetFlag(DayOfWeek.Saturday, 2, G(17));
                // Sábado no tiene columna de Turno 3 -> nunca planeado
                plan.SetFlag(DayOfWeek.Sunday, 1, G(18));
                plan.SetFlag(DayOfWeek.Sunday, 2, G(19));
                // Domingo tampoco tiene columna de Turno 3

                plans[groupId] = plan;
            }

            _plansByGroup = plans;
            _plansForMonday = monday;
            _plansLoadedAt = DateTime.UtcNow;
            _hasDataForCurrentWeek = found;

            if (!found)
            {
                _logger.LogInformation("Heijunka_Plan_List no tiene filas para la semana del {Monday} — se usa el criterio viejo (Planned_Shift_for_OEE != 0) mientras tanto.", monday);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cargando Heijunka_Plan_List para la semana del {Monday}", monday);
        }
        finally
        {
            _planLock.Release();
        }
    }
}
