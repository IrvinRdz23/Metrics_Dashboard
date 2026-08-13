namespace Metrics_Dashboard.Models;

/// <summary>
/// Banderas de Heijunka_Plan_List de UN Product_List_Group_ID para la semana vigente —
/// una por (día de la semana, turno). Sábado y Domingo no tienen columna de Turno 3 en la
/// tabla, así que esa combinación simplemente nunca puede salir "planeada".
/// </summary>
public class HeijunkaGroupPlan
{
    public int ProductListGroupId { get; set; }

    private readonly Dictionary<(DayOfWeek Day, int ShiftId), bool> _flags = new();

    public void SetFlag(DayOfWeek day, int shiftId, bool value) => _flags[(day, shiftId)] = value;

    public bool IsPlanned(DayOfWeek day, int shiftId) => _flags.TryGetValue((day, shiftId), out var v) && v;
}
