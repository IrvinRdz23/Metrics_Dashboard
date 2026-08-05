using Microsoft.AspNetCore.Mvc;
using Metrics_Dashboard.Services;

namespace Metrics_Dashboard.Controllers;

/// <summary>
/// Una sola ruta dinámica (/Line/{id}) para TODAS las líneas de la planta — no tiene
/// caso tener 53 archivos de vista ni 53 acciones. El "id" es el Product_List_ID real
/// de tu base; se copia de aquí para configurar cada TV de piso una sola vez.
/// </summary>
public class LineController : Controller
{
    private readonly ILineDetailService _lineService;

    public LineController(ILineDetailService lineService)
    {
        _lineService = lineService;
    }

    [Route("/Line/{id:int}")]
    public async Task<IActionResult> Index(int id, CancellationToken ct)
    {
        var snapshot = await _lineService.GetSnapshotAsync(id, ct);
        return View(snapshot);
    }
}
