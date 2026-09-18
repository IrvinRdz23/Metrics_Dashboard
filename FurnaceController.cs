using Microsoft.AspNetCore.Mvc;
using Metrics_Dashboard.Services;

namespace Metrics_Dashboard.Controllers;

/// <summary>
/// Un solo controller para los 6 dashboards de detalle. Cada acción llama al mismo
/// IFurnaceDetailService con distinto furnaceId y regresa su propia vista (por convención
/// de ASP.NET, la acción "Furnace1" resuelve a Views/Furnace/Furnace1.cshtml, etc.),
/// así que cada horno tiene su propia URL y su propio archivo de vista con nombre real.
///
/// Ahora todas aceptan ?date=YYYY-MM-DD&shiftId=1|2|3 opcional: si vienen, se sirve el
/// histórico de ese día/turno (mismo patrón que /Dashboard/Historical) en vez de en vivo.
/// Sin querystring, sigue siendo el dashboard en vivo de siempre. Esto es lo que permite
/// que, viniendo del histórico general, el horno se abra en SU histórico del mismo día/turno.
/// </summary>
public class FurnaceController : Controller
{
    private readonly IFurnaceDetailService _furnaceService;

    public FurnaceController(IFurnaceDetailService furnaceService)
    {
        _furnaceService = furnaceService;
    }

    private async Task<IActionResult> ServeAsync(int furnaceId, DateTime? date, int? shiftId, CancellationToken ct)
    {
        if (date == null || shiftId == null)
        {
            return View(ViewName(furnaceId), await _furnaceService.GetSnapshotAsync(furnaceId, ct));
        }

        var today = DateTime.Today;
        var selectedDate = date.Value.Date > today ? today : date.Value.Date;
        var selectedShift = (shiftId is >= 1 and <= 3) ? shiftId.Value : 1;

        ViewData["IsHistorical"] = true;
        ViewData["HistoricalDate"] = selectedDate.ToString("yyyy-MM-dd");
        ViewData["HistoricalShiftId"] = selectedShift;

        return View(ViewName(furnaceId), await _furnaceService.GetHistoricalSnapshotAsync(furnaceId, selectedDate, selectedShift, ct));
    }

    // El nombre de la vista es siempre el mismo por convención de acción -> pero como aquí
    // se resuelve a mano (ViewName explícito) por compartir un solo método privado, hay que
    // decírselo directo a View().
    private static string ViewName(int furnaceId) => furnaceId == 6 ? "TubeMills" : $"Furnace{furnaceId}";

    [Route("/Furnace1")]
    public Task<IActionResult> Furnace1(DateTime? date, int? shiftId, CancellationToken ct) => ServeAsync(1, date, shiftId, ct);

    [Route("/Furnace2")]
    public Task<IActionResult> Furnace2(DateTime? date, int? shiftId, CancellationToken ct) => ServeAsync(2, date, shiftId, ct);

    [Route("/Furnace3")]
    public Task<IActionResult> Furnace3(DateTime? date, int? shiftId, CancellationToken ct) => ServeAsync(3, date, shiftId, ct);

    [Route("/Furnace4")]
    public Task<IActionResult> Furnace4(DateTime? date, int? shiftId, CancellationToken ct) => ServeAsync(4, date, shiftId, ct);

    [Route("/Furnace5")]
    public Task<IActionResult> Furnace5(DateTime? date, int? shiftId, CancellationToken ct) => ServeAsync(5, date, shiftId, ct);

    [Route("/TubeMills")]
    public Task<IActionResult> TubeMills(DateTime? date, int? shiftId, CancellationToken ct) => ServeAsync(6, date, shiftId, ct);
}
