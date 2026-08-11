using Microsoft.AspNetCore.Mvc;
using Metrics_Dashboard.Services;

namespace Metrics_Dashboard.Controllers;

[Route("/OeeHistory")]
public class OeeHistoryController : Controller
{
    private readonly IOeeHistoryService _historyService;

    public OeeHistoryController(IOeeHistoryService historyService)
    {
        _historyService = historyService;
    }

    // GET /OeeHistory  -> vista, precargada con Diario (últimos 14 días) para el primer render
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var bars = await _historyService.GetDailyHistoryAsync(14, ct);
        return View(bars);
    }

    // GET /OeeHistory/Data?level=shift|daily|weekly|monthly&count=N
    [HttpGet("Data")]
    public async Task<IActionResult> Data(string level, int count, CancellationToken ct)
    {
        var bars = (level?.ToLowerInvariant()) switch
        {
            "shift" => await _historyService.GetShiftHistoryAsync(count > 0 ? count : 15, ct),
            "weekly" => await _historyService.GetWeeklyHistoryAsync(count > 0 ? count : 12, ct),
            "monthly" => await _historyService.GetMonthlyHistoryAsync(count > 0 ? count : 12, ct),
            _ => await _historyService.GetDailyHistoryAsync(count > 0 ? count : 14, ct)
        };
        return Json(bars);
    }

    // GET /OeeHistory/Detail?level=...&date=2026-08-04&shiftId=1
    [HttpGet("Detail")]
    public async Task<IActionResult> Detail(string level, DateTime date, int? shiftId, CancellationToken ct)
    {
        var bars = (level?.ToLowerInvariant()) switch
        {
            "shift" => await _historyService.GetShiftDetailAsync(date, shiftId ?? 1, ct),
            "weekly" => await _historyService.GetWeeklyDetailAsync(date, ct),
            "monthly" => await _historyService.GetMonthlyDetailAsync(date.Year, date.Month, ct),
            _ => await _historyService.GetDailyDetailAsync(date, ct)
        };
        return Json(bars);
    }
}
