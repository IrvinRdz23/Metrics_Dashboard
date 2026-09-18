using Microsoft.AspNetCore.Mvc;
using Metrics_Dashboard.Services;

namespace Metrics_Dashboard.Controllers;

/// <summary>
/// Una sola ruta dinámica (/Line/{id}) para TODAS las líneas de la planta — no tiene
/// caso tener 53 archivos de vista ni 53 acciones. El "id" es el Product_List_ID real
/// de tu base; se copia de aquí para configurar cada TV de piso una sola vez.
///
/// Acepta ?date=YYYY-MM-DD&shiftId=1|2|3 opcional: si vienen, sirve el histórico de esa
/// línea para ese día/turno (mismo patrón que /Dashboard/Historical y los hornos) en vez
/// de en vivo — así, viniendo del histórico de un horno, la línea se abre en su mismo
/// día/turno.
/// </summary>
public class LineController : Controller
{
    private readonly ILineDetailService _lineService;

    public LineController(ILineDetailService lineService)
    {
        _lineService = lineService;
    }

    [Route("/Line/{id:int}")]
    public async Task<IActionResult> Index(int id, DateTime? date, int? shiftId, CancellationToken ct)
    {
        if (date == null || shiftId == null)
        {
            return View(await _lineService.GetSnapshotAsync(id, ct));
        }

        var today = DateTime.Today;
        var selectedDate = date.Value.Date > today ? today : date.Value.Date;
        var selectedShift = (shiftId is >= 1 and <= 3) ? shiftId.Value : 1;

        ViewData["IsHistorical"] = true;
        ViewData["HistoricalDate"] = selectedDate.ToString("yyyy-MM-dd");
        ViewData["HistoricalShiftId"] = selectedShift;

        var snapshot = await _lineService.GetHistoricalSnapshotAsync(id, selectedDate, selectedShift, ct);
        return View(snapshot);
    }
}
