using Metrics_Dashboard.Models;
using Metrics_Dashboard.Services;
using Microsoft.AspNetCore.Mvc;

namespace Metrics_Dashboard.Controllers;

public class GroupController : Controller
{
    private readonly IGroupDetailService _groups;
    private readonly IMetricsRawDataService _raw;

    public GroupController(IGroupDetailService groups, IMetricsRawDataService raw)
    {
        _groups = groups;
        _raw = raw;
    }

    /// <summary>Detalle de un grupo. Con ?date=&amp;shiftId= muestra ese turno histórico.</summary>
    [HttpGet("Group/{id:int}")]
    public async Task<IActionResult> Index(int id, DateTime? date, int? shiftId, CancellationToken ct)
    {
        if (!ProductGroupCatalog.IsValid(id)) return NotFound();
        var (snapshot, shifts) = await Load(id, date, shiftId, ct);
        ViewData["Shifts"] = shifts;
        return View(snapshot);
    }

    /// <summary>JSON para el refresco automático de la vista.</summary>
    [HttpGet("Group/{id:int}/Data")]
    public async Task<IActionResult> Data(int id, DateTime? date, int? shiftId, CancellationToken ct)
    {
        if (!ProductGroupCatalog.IsValid(id)) return NotFound();
        var (snapshot, _) = await Load(id, date, shiftId, ct);
        return Json(snapshot);
    }

    private async Task<(GroupDetailSnapshot Snapshot, List<ShiftInfo> Shifts)> Load(int id, DateTime? date, int? shiftId, CancellationToken ct)
    {
        if (date == null && shiftId == null)
            return (await _groups.GetSnapshotAsync(id, ct), new List<ShiftInfo>());

        var day = (date ?? DateTime.Today).Date;
        var (resolvedShift, shifts) = await _raw.ResolveShiftAsync(day, shiftId, ct);
        return (await _groups.GetHistoricalSnapshotAsync(id, day, resolvedShift, ct), shifts);
    }
}
