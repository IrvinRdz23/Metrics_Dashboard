using Microsoft.AspNetCore.Mvc;
using Metrics_Dashboard.Services;

namespace Metrics_Dashboard.Controllers;

/// <summary>
/// Un solo controller para los 6 dashboards de detalle. Cada acción llama al mismo
/// IFurnaceDetailService con distinto furnaceId y regresa su propia vista (por convención
/// de ASP.NET, la acción "Furnace1" resuelve a Views/Furnace/Furnace1.cshtml, etc.),
/// así que cada horno tiene su propia URL y su propio archivo de vista con nombre real.
/// </summary>
public class FurnaceController : Controller
{
    private readonly IFurnaceDetailService _furnaceService;

    public FurnaceController(IFurnaceDetailService furnaceService)
    {
        _furnaceService = furnaceService;
    }

    [Route("/Furnace1")]
    public async Task<IActionResult> Furnace1(CancellationToken ct) => View(await _furnaceService.GetSnapshotAsync(1, ct));

    [Route("/Furnace2")]
    public async Task<IActionResult> Furnace2(CancellationToken ct) => View(await _furnaceService.GetSnapshotAsync(2, ct));

    [Route("/Furnace3")]
    public async Task<IActionResult> Furnace3(CancellationToken ct) => View(await _furnaceService.GetSnapshotAsync(3, ct));

    [Route("/Furnace4")]
    public async Task<IActionResult> Furnace4(CancellationToken ct) => View(await _furnaceService.GetSnapshotAsync(4, ct));

    [Route("/Furnace5")]
    public async Task<IActionResult> Furnace5(CancellationToken ct) => View(await _furnaceService.GetSnapshotAsync(5, ct));

    [Route("/TubeMills")]
    public async Task<IActionResult> TubeMills(CancellationToken ct) => View(await _furnaceService.GetSnapshotAsync(6, ct));
}
