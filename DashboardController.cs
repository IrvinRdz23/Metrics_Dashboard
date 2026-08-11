using Microsoft.AspNetCore.Mvc;
using Metrics_Dashboard.Services;

namespace Metrics_Dashboard.Controllers;

public class DashboardController : Controller
{
    private readonly IPlantMetricsService _metricsService;

    // Límites del selector de fecha del histórico: nunca hoy (no ha terminado el día),
    // nunca el futuro, y hasta 90 días atrás para que el picker no sea gigante.
    private const int HistoricalLookbackDays = 90;

    public DashboardController(IPlantMetricsService metricsService)
    {
        _metricsService = metricsService;
    }

    // GET /  y  /Dashboard  -> Dashboard General en vivo (5 hornos + Tube Mills alternando)
    [Route("/")]
    [Route("/Dashboard")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var snapshot = await _metricsService.GetSnapshotAsync(ct);
        return View(snapshot);
    }

    // GET /Dashboard/Historical?date=2026-08-10&shiftId=1
    // Sin querystring -> por default muestra AYER, Turno 1 (para no dejar una pantalla vacía
    // pidiendo que elijan algo primero). El picker de fecha/turno vive en el header de la vista.
    [Route("/Dashboard/Historical")]
    public async Task<IActionResult> Historical(DateTime? date, int? shiftId, CancellationToken ct)
    {
        var today = DateTime.Today;
        var minDate = today.AddDays(-HistoricalLookbackDays);

        var selectedDate = (date ?? today.AddDays(-1)).Date;
        if (selectedDate >= today) selectedDate = today.AddDays(-1);   // nunca hoy ni futuro
        if (selectedDate < minDate) selectedDate = minDate;

        var selectedShift = (shiftId is >= 1 and <= 3) ? shiftId!.Value : 1;

        var snapshot = await _metricsService.GetHistoricalSnapshotAsync(selectedDate, selectedShift, ct);

        ViewData["MinDate"] = minDate.ToString("yyyy-MM-dd");
        ViewData["MaxDate"] = today.AddDays(-1).ToString("yyyy-MM-dd");

        return View(snapshot);
    }
}
