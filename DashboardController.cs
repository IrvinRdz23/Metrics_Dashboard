using Microsoft.AspNetCore.Mvc;
using Metrics_Dashboard.Services;
using System.Text.RegularExpressions;

namespace Metrics_Dashboard.Controllers;

public class DashboardController : Controller
{
    private readonly IPlantMetricsService _metricsService;

    // Límites del selector de fecha del histórico: nunca el futuro, y hasta 90 días atrás
    // para que el picker no sea gigante. "Hoy" ahora SÍ puede ser el máximo — pero solo si
    // ya hay al menos un turno de hoy que haya terminado (ver Historical()).
    private const int HistoricalLookbackDays = 90;
    private static readonly Regex ShiftNumberRegex = new(@"Shift\s+(\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    //
    // Default SIN querystring, ahora depende de en qué turno estás parado ahora mismo:
    //   - Si vas en Turno 1 (o no se pudo detectar) -> Turno 1 de AYER, como siempre (hoy no
    //     tiene ningún turno cerrado todavía).
    //   - Si vas en Turno 2 -> Turno 1 de HOY (ya cerró).
    //   - Si vas en Turno 3 -> Turno 2 de HOY (ya cerró).
    //
    // También se puede pedir HOY a mano con cualquier turno que YA haya terminado — nunca
    // el turno que está corriendo ahora mismo ni uno futuro; si piden algo inválido para
    // hoy, se cae de regreso a ayer con ese mismo turno en vez de enseñar un turno a medias
    // como si fuera un histórico cerrado.
    [Route("/Dashboard/Historical")]
    public async Task<IActionResult> Historical(DateTime? date, int? shiftId, CancellationToken ct)
    {
        var today = DateTime.Today;
        var minDate = today.AddDays(-HistoricalLookbackDays);
        var currentShiftId = await GetCurrentShiftIdAsync(ct);

        DateTime selectedDate;
        int selectedShift;

        if (date == null && shiftId == null)
        {
            if (currentShiftId is null or 1)
            {
                selectedDate = today.AddDays(-1);
                selectedShift = 1;
            }
            else
            {
                selectedDate = today;
                selectedShift = currentShiftId.Value - 1;
            }
        }
        else
        {
            selectedDate = (date ?? today.AddDays(-1)).Date;
            selectedShift = (shiftId is >= 1 and <= 3) ? shiftId!.Value : 1;
        }

        if (selectedDate > today) selectedDate = today;
        if (selectedDate < minDate) selectedDate = minDate;

        // "Hoy" solo es válido con un turno que YA terminó (estrictamente antes del que está
        // corriendo ahora). Si piden hoy con el turno en curso (o uno futuro), o no se pudo
        // detectar el turno actual, se cae a ayer con ese mismo número de turno.
        if (selectedDate == today && (currentShiftId == null || selectedShift >= currentShiftId.Value))
        {
            selectedDate = today.AddDays(-1);
        }

        var snapshot = await _metricsService.GetHistoricalSnapshotAsync(selectedDate, selectedShift, ct);

        ViewData["MinDate"] = minDate.ToString("yyyy-MM-dd");
        // "Hoy" solo se ofrece como máximo del picker si de verdad hay algo que ver ahí
        // (al menos el Turno 1 ya cerrado) — si no, se deja igual que antes (máximo ayer).
        ViewData["MaxDate"] = (currentShiftId is > 1)
            ? today.ToString("yyyy-MM-dd")
            : today.AddDays(-1).ToString("yyyy-MM-dd");
        ViewData["Today"] = today.ToString("yyyy-MM-dd");
        ViewData["CurrentShiftId"] = currentShiftId;

        return View(snapshot);
    }

    /// <summary>¿Qué turno está corriendo AHORA MISMO? Se detecta del mismo snapshot en vivo
    /// que ya usa el dashboard general — no hace falta lógica nueva de detección de turno,
    /// solo leer el número del Shift_Desc que ya trae ("Shift 2 15:15-23:25" -> 2). Null si
    /// no se pudo determinar (ej. hueco entre turnos, o error consultando el SP).</summary>
    private async Task<int?> GetCurrentShiftIdAsync(CancellationToken ct)
    {
        try
        {
            var live = await _metricsService.GetSnapshotAsync(ct);
            var m = ShiftNumberRegex.Match(live.ShiftDesc ?? string.Empty);
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
        catch
        {
            return null;
        }
    }
}
