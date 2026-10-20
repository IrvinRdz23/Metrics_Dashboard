using Metrics_Dashboard.Models;
using Microsoft.Extensions.Options;

namespace Metrics_Dashboard.Services;

public interface IPlantMetricsService
{
    /// <summary>Snapshot en vivo del turno en curso.</summary>
    Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>Snapshot de un turno ya registrado (histórico) — mismas reglas que el vivo.</summary>
    Task<PlantDashboardSnapshot> GetHistoricalSnapshotAsync(DateTime date, int shiftId, CancellationToken ct = default);

    /// <summary>Construye el snapshot desde filas ya obtenidas (de UN solo turno).</summary>
    Task<PlantDashboardSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, CancellationToken ct = default, bool isLive = true);
}

/// <summary>
/// Dashboard general de Planta 4: 4 grupos (Condenser, Battery Cooling Plate, Heater Core,
/// Residential). Por grupo: producción, plan, OEE (promedio de líneas que cuentan), SAP % y la
/// tendencia hora por hora. Heijunka (si está encendido) decide qué línea cuenta; si no, cuenta
/// toda línea con Planned_Shift_for_OEE &gt; 0.
/// </summary>
public class PlantMetricsService : IPlantMetricsService
{
    private readonly IMetricsRawDataService _raw;
    private readonly IHeijunkaService _heijunka;
    private readonly PlantOptions _options;
    private readonly ILogger<PlantMetricsService> _logger;

    public PlantMetricsService(IMetricsRawDataService raw, IHeijunkaService heijunka, IOptions<PlantOptions> options, ILogger<PlantMetricsService> logger)
    {
        _raw = raw;
        _heijunka = heijunka;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PlantDashboardSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _raw.FetchCurrentShiftRowsAsync(ct);
            return await BuildFromRowsAsync(rows, shiftDesc, ct, isLive: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo el snapshot de Plant_Metrics_Production_Reports");
            return EmptySnapshot();
        }
    }

    public async Task<PlantDashboardSnapshot> GetHistoricalSnapshotAsync(DateTime date, int shiftId, CancellationToken ct = default)
    {
        try
        {
            var dayRows = await _raw.FetchDayRowsAsync(date, ct);
            var rows = dayRows.Where(r => r.ShiftId == shiftId).ToList();
            var shiftDesc = rows.Select(r => r.ShiftDesc).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty;

            var snap = await BuildFromRowsAsync(rows, shiftDesc, ct, isLive: false);
            snap.IsHistorical = true;
            snap.HistoricalDate = date.Date;
            snap.HistoricalShiftId = shiftId;
            return snap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo el histórico {Date} turno {Shift}", date, shiftId);
            var empty = EmptySnapshot();
            empty.IsHistorical = true;
            empty.HistoricalDate = date.Date;
            empty.HistoricalShiftId = shiftId;
            return empty;
        }
    }

    public async Task<PlantDashboardSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, CancellationToken ct = default, bool isLive = true)
    {
        var now = DateTime.Now;
        var eventDate = GroupBuilder.EventDateOf(rows);
        var shiftId = rows.Select(r => r.ShiftId).FirstOrDefault(i => i > 0);

        var snapshot = new PlantDashboardSnapshot
        {
            PlantName = _options.PlantName,
            ShiftDesc = shiftDesc,
            ShiftId = shiftId,
            ShiftDurationHours = ShiftTimeHelper.GetDurationHours(shiftDesc),
            EventDate = eventDate,
            HeijunkaEnabled = _options.HeijunkaEnabled,
        };

        // Líneas de los 4 grupos y UN solo llamado a Heijunka para todas.
        var linesByGroup = ProductGroupCatalog.Groups.ToDictionary(g => g.Id, g => GroupBuilder.BuildLines(rows, g.Id));
        var heijunka = await _heijunka.IsPlannedBatchAsync(
            linesByGroup.Values.SelectMany(l => l).Select(l => l.ProductListId), eventDate, shiftId, ct);

        foreach (var (groupId, lines) in linesByGroup)
        {
            GroupBuilder.ApplyHeijunka(lines, heijunka);
            snapshot.Groups.Add(GroupBuilder.CreateGroup(rows, groupId, lines, shiftDesc, isLive, now));
        }

        return snapshot;
    }

    private PlantDashboardSnapshot EmptySnapshot() => new()
    {
        PlantName = _options.PlantName,
        HeijunkaEnabled = _options.HeijunkaEnabled,
        Groups = ProductGroupCatalog.Groups
            .Select((g, i) => new GroupMetric { GroupId = g.Id, GroupName = g.Name, ColorIndex = i })
            .ToList()
    };
}
