using Metrics_Dashboard.Models;
using Microsoft.Extensions.Options;

namespace Metrics_Dashboard.Services;

public interface IGroupDetailService
{
    Task<GroupDetailSnapshot> GetSnapshotAsync(int groupId, CancellationToken ct = default);
    Task<GroupDetailSnapshot> GetHistoricalSnapshotAsync(int groupId, DateTime date, int shiftId, CancellationToken ct = default);
    Task<GroupDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int groupId, CancellationToken ct = default, bool isLive = true);
}

/// <summary>Detalle de un grupo con TODAS sus líneas. Misma lógica que el dashboard general (GroupBuilder).</summary>
public class GroupDetailService : IGroupDetailService
{
    private readonly IMetricsRawDataService _raw;
    private readonly IHeijunkaService _heijunka;
    private readonly PlantOptions _options;
    private readonly ILogger<GroupDetailService> _logger;

    public GroupDetailService(IMetricsRawDataService raw, IHeijunkaService heijunka, IOptions<PlantOptions> options, ILogger<GroupDetailService> logger)
    {
        _raw = raw;
        _heijunka = heijunka;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GroupDetailSnapshot> GetSnapshotAsync(int groupId, CancellationToken ct = default)
    {
        try
        {
            var (rows, shiftDesc) = await _raw.FetchCurrentShiftRowsAsync(ct);
            return await BuildFromRowsAsync(rows, shiftDesc, groupId, ct, isLive: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo el detalle del grupo {GroupId}", groupId);
            return EmptySnapshot(groupId);
        }
    }

    public async Task<GroupDetailSnapshot> GetHistoricalSnapshotAsync(int groupId, DateTime date, int shiftId, CancellationToken ct = default)
    {
        try
        {
            var dayRows = await _raw.FetchDayRowsAsync(date, ct);
            var rows = dayRows.Where(r => r.ShiftId == shiftId).ToList();
            var shiftDesc = rows.Select(r => r.ShiftDesc).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty;

            var snap = await BuildFromRowsAsync(rows, shiftDesc, groupId, ct, isLive: false);
            snap.IsHistorical = true;
            snap.HistoricalDate = date.Date;
            snap.HistoricalShiftId = shiftId;
            return snap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo el histórico del grupo {GroupId}", groupId);
            var empty = EmptySnapshot(groupId);
            empty.IsHistorical = true;
            empty.HistoricalDate = date.Date;
            empty.HistoricalShiftId = shiftId;
            return empty;
        }
    }

    public async Task<GroupDetailSnapshot> BuildFromRowsAsync(List<RawMetricRow> rows, string shiftDesc, int groupId, CancellationToken ct = default, bool isLive = true)
    {
        if (!ProductGroupCatalog.IsValid(groupId)) return EmptySnapshot(groupId);

        var eventDate = GroupBuilder.EventDateOf(rows);
        var shiftId = rows.Select(r => r.ShiftId).FirstOrDefault(i => i > 0);

        var lines = GroupBuilder.BuildLines(rows, groupId);
        var heijunka = await _heijunka.IsPlannedBatchAsync(lines.Select(l => l.ProductListId), eventDate, shiftId, ct);
        GroupBuilder.ApplyHeijunka(lines, heijunka);

        return new GroupDetailSnapshot
        {
            PlantName = _options.PlantName,
            ShiftDesc = shiftDesc,
            ShiftId = shiftId,
            ShiftDurationHours = ShiftTimeHelper.GetDurationHours(shiftDesc),
            EventDate = eventDate,
            HeijunkaEnabled = _options.HeijunkaEnabled,
            Group = GroupBuilder.CreateGroup(rows, groupId, lines, shiftDesc, isLive, DateTime.Now)
        };
    }

    private GroupDetailSnapshot EmptySnapshot(int groupId) => new()
    {
        PlantName = _options.PlantName,
        HeijunkaEnabled = _options.HeijunkaEnabled,
        Group = new GroupMetric
        {
            GroupId = groupId,
            GroupName = ProductGroupCatalog.NameOf(groupId),
            ColorIndex = ProductGroupCatalog.IndexOf(groupId)
        }
    };
}
