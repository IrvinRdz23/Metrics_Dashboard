using Microsoft.AspNetCore.Mvc;
using PlantMetricsDashboard.Services;

namespace PlantMetricsDashboard.Controllers;

public class DashboardController : Controller
{
    private readonly IPlantMetricsService _metricsService;

    public DashboardController(IPlantMetricsService metricsService)
    {
        _metricsService = metricsService;
    }

    // GET /  y  /Dashboard  -> Dashboard General (los 5 hornos)
    [Route("/")]
    [Route("/Dashboard")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var snapshot = await _metricsService.GetSnapshotAsync(ct);
        return View(snapshot);
    }
}
