using Metrics_Dashboard.Models;
using Metrics_Dashboard.Services;
using Microsoft.AspNetCore.Mvc;

namespace Metrics_Dashboard.Controllers;

public class LineController : Controller
{
    private readonly ILineDetailService _lines;
    private readonly IMetricsRawDataService _raw;

    public LineController(ILineDetailService lines, IMetricsRawDataService raw)
    {
        _lines = lines;
        _raw = raw;
    }

    /// <summary>TV de una línea por Product_List_ID. Con ?date=&amp;shiftId= muestra ese turno histórico.</summary>
    [HttpGet("Line/{id:int}")]
    public async Task<IActionResult> Index(int id, DateTime? date, int? shiftId, CancellationToken ct)
    {
        var (snapshot, shifts) = await Load(id, date, shiftId, ct);
        ViewData["Shifts"] = shifts;
        return View(snapshot);
    }

    [HttpGet("Line/{id:int}/Data")]
    public async Task<IActionResult> Data(int id, DateTime? date, int? shiftId, CancellationToken ct)
    {
        var (snapshot, _) = await Load(id, date, shiftId, ct);
        return Json(snapshot);
    }

    private async Task<(LineDetailSnapshot Snapshot, List<ShiftInfo> Shifts)> Load(int id, DateTime? date, int? shiftId, CancellationToken ct)
    {
        if (date == null && shiftId == null)
            return (await _lines.GetSnapshotAsync(id, ct), new List<ShiftInfo>());

        var day = (date ?? DateTime.Today).Date;
        var (resolvedShift, shifts) = await _raw.ResolveShiftAsync(day, shiftId, ct);
        return (await _lines.GetHistoricalSnapshotAsync(id, day, resolvedShift, ct), shifts);
    }
}
