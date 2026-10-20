using System.Globalization;
using Metrics_Dashboard.Models;

namespace Metrics_Dashboard.Services;

/// <summary>
/// Lógica común (dashboard general, detalle de grupo y TV de línea) para convertir las filas
/// crudas del SP en líneas, tendencias y grupos. Un solo lugar = las 3 vistas siempre cuadran.
/// </summary>
public static class GroupBuilder
{
    public static DateTime EventDateOf(IEnumerable<RawMetricRow> rows)
    {
        var s = rows.Select(r => r.EventDateShort).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (s != null &&
            (DateTime.TryParseExact(s.Trim(), "yyyy/MM/dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ||
             DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)))
            return d.Date;
        return DateTime.Today;
    }

    /// <summary>
    /// Líneas de un grupo desde Report_Group 1 (+ SAP de Report_Group 3). El SP a veces regresa la
    /// MISMA línea 2 veces (join interno a Heijunka_Plan_List): se queda la fila con datos reales.
    /// </summary>
    public static List<ProductLineMetric> BuildLines(IReadOnlyCollection<RawMetricRow> rows, int groupId)
    {
        var lookup = rows.BuildProductListIdLookup();
        var index = new Dictionary<string, ProductLineMetric>();
        var lines = new List<ProductLineMetric>();

        foreach (var r in rows.Where(r => r.ReportGroup == 1 && r.GroupId == groupId))
        {
            if (index.TryGetValue(r.Desc, out var existing))
            {
                if (r.Total > existing.Total || r.PlannedForOee > existing.PlannedShift)
                {
                    existing.Total = r.Total;
                    existing.AccumulatedRate = r.AccumRate;
                    existing.PlannedShift = r.PlannedForOee;
                    existing.OeeShift = r.OeeShift;
                    existing.CycleTimeSecs = r.CycleTimeSecs;
                }
                continue;
            }

            var line = new ProductLineMetric
            {
                ProductDesc = r.Desc,
                GroupId = groupId,
                ProductListId = lookup.TryGetValue((groupId, r.Desc), out var id) ? id : 0,
                ProductOrder = r.ProductOrder,
                CycleTimeSecs = r.CycleTimeSecs,
                Total = r.Total,
                AccumulatedRate = r.AccumRate,
                PlannedShift = r.PlannedForOee,
                OeeShift = r.OeeShift,
            };
            index[r.Desc] = line;
            lines.Add(line);
        }

        // SAP: por Product_List_ID se toma el máximo (por si el SP duplica la fila) y se suma por descripción.
        var sapRows = rows.Where(r => r.ReportGroup == 3 && r.GroupId == groupId && r.TotalSap > 0)
            .GroupBy(r => (r.Desc, Key: r.ProductListId > 0 ? r.ProductListId.ToString() : r.Desc))
            .Select(g => (g.Key.Desc, Sap: g.Max(x => x.TotalSap)))
            .GroupBy(x => x.Desc);
        foreach (var g in sapRows)
        {
            if (index.TryGetValue(g.Key, out var line)) line.TotalSap = g.Sum(x => x.Sap);
        }

        return lines.OrderBy(l => l.ProductOrder).ToList();
    }

    public static void ApplyHeijunka(IEnumerable<ProductLineMetric> lines, Dictionary<int, bool> results)
    {
        foreach (var l in lines)
            l.HeijunkaPlanned = l.ProductListId > 0 && results.TryGetValue(l.ProductListId, out var p) ? p : null;
    }

    /// <summary>
    /// Hora por hora de UNA línea (Report_Group 2). Si el SP duplica la fila de una hora se toma el
    /// máximo, no la suma. SAP por hora es aproximado: proporcional a la producción de la línea.
    /// </summary>
    public static List<HourlyPoint> LineHourly(IEnumerable<RawMetricRow> rows, ProductLineMetric line)
    {
        var sapRatio = line.Total > 0 ? Math.Min(1.0, (double)line.TotalSap / line.Total) : 0;
        return rows
            .Where(r => r.ReportGroup == 2 && r.GroupId == line.GroupId && r.Desc == line.ProductDesc
                        && !string.IsNullOrWhiteSpace(r.Hour) && r.Hour != "-")
            .GroupBy(r => ShiftTimeHelper.NormalizeHour(r.Hour))
            .Select(g =>
            {
                var prod = g.Max(x => x.Total);
                return new HourlyPoint { Hour = g.Key, Production = prod, SapProduction = (int)Math.Round(prod * sapRatio) };
            })
            .ToList();
    }

    /// <summary>Tendencia del grupo: SOLO líneas que cuentan (las no planeadas jamás entran), sumadas hora por hora.</summary>
    public static List<HourlyPoint> GroupTrend(IReadOnlyCollection<RawMetricRow> rows, List<ProductLineMetric> counted,
        string shiftDesc, bool isLive, DateTime now, int plan)
    {
        var all = new List<HourlyPoint>();
        foreach (var line in counted) all.AddRange(LineHourly(rows, line));
        return ShiftTimeHelper.FillTrend(all, shiftDesc, isLive, now, plan);
    }

    public static GroupMetric CreateGroup(IReadOnlyCollection<RawMetricRow> rows, int groupId, List<ProductLineMetric> lines,
        string shiftDesc, bool isLive, DateTime now)
    {
        var group = new GroupMetric
        {
            GroupId = groupId,
            GroupName = ProductGroupCatalog.NameOf(groupId),
            ColorIndex = ProductGroupCatalog.IndexOf(groupId),
            Lines = lines
        };
        var counted = lines.Where(l => l.CountsForStats).ToList();
        group.HourlyTrend = GroupTrend(rows, counted, shiftDesc, isLive, now, counted.Sum(l => l.PlannedShift));
        return group;
    }
}
