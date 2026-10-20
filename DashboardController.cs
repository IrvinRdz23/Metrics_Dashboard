using Metrics_Dashboard.Models;
using Metrics_Dashboard.Services;
using Microsoft.AspNetCore.Mvc;

namespace Metrics_Dashboard.Controllers;

public class DashboardController : Controller
{
    private readonly IPlantMetricsService _plant;
    private readonly IMetricsRawDataService _raw;

    public DashboardController(IPlantMetricsService plant, IMetricsRawDataService raw)
    {
        _plant = plant;
        _raw = raw;
    }

    /// <summary>Dashboard en vivo (turno en curso). Se actualiza por SignalR.</summary>
    [HttpGet("")]
    [HttpGet("Dashboard")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var snapshot = await _plant.GetSnapshotAsync(ct);
        ViewData["Shifts"] = new List<ShiftInfo>();
        return View(snapshot);
    }

    /// <summary>
    /// Histórico por turno: /Historical?date=2026-08-13&amp;shiftId=1. Sin parámetros abre HOY con el
    /// último turno ya cerrado. Mismas reglas y mismo diseño que el vivo, pero sin SignalR.
    /// </summary>
    [HttpGet("Historical")]
    public async Task<IActionResult> Historical(DateTime? date, int? shiftId, CancellationToken ct)
    {
        var day = (date ?? DateTime.Today).Date;
        var (resolvedShift, shifts) = await _raw.ResolveShiftAsync(day, shiftId, ct);
        var snapshot = await _plant.GetHistoricalSnapshotAsync(day, resolvedShift, ct);
        ViewData["Shifts"] = shifts;
        return View("Index", snapshot);
    }
}
