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

    // GET /OeeHistory  -> vista, precargada con Diario (esta semana + la pasada)
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var bars = await _historyService.GetDailyHistoryAsync(ct);
        return View(bars);
    }

    // GET /OeeHistory/Data?level=shift|daily|weekly|monthly
    [HttpGet("Data")]
    public async Task<IActionResult> Data(string level, CancellationToken ct)
    {
        var bars = (level?.ToLowerInvariant()) switch
        {
            "shift" => await _historyService.GetShiftHistoryAsync(ct),
            "weekly" => await _historyService.GetWeeklyHistoryAsync(ct),
            "monthly" => await _historyService.GetMonthlyHistoryAsync(ct),
            _ => await _historyService.GetDailyHistoryAsync(ct)
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
