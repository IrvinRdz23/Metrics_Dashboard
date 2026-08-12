using Metrics_Dashboard.Models;
using System.Data.SqlClient;

namespace Metrics_Dashboard.Services;

public interface IOeeHistoryStorageService
{
    Task<List<RawMetricRow>> GetStoredDayAsync(DateTime date, CancellationToken ct = default);
    Task<bool> IsDayStoredAsync(DateTime date, CancellationToken ct = default);

    /// <summary>Borra lo que hubiera de ese día y guarda las líneas nuevas — un día completo se
    /// reemplaza entero, no se hace upsert línea por línea (más simple y seguro).</summary>
    Task UpsertDayAsync(DateTime date, List<RawMetricRow> rawRows, CancellationToken ct = default);

    Task<(DateTime? OldestDate, int ConsecutiveEmptyDays, bool IsComplete)> GetBackfillStateAsync(CancellationToken ct = default);
    Task UpdateBackfillProgressAsync(DateTime? oldestDate, int consecutiveEmptyDays, bool isComplete, CancellationToken ct = default);
}

/// <summary>
/// Toda la lectura/escritura de PlantMetrics_OeeHistory (y su tabla de progreso de backfill).
/// Nada de esto toca el SP — es una tabla propia que solo guarda turnos YA TERMINADOS.
/// </summary>
public class OeeHistoryStorageService : IOeeHistoryStorageService
{
    private readonly string _connectionString;
    private readonly ILogger<OeeHistoryStorageService> _logger;

    public OeeHistoryStorageService(IConfiguration config, ILogger<OeeHistoryStorageService> logger)
    {
        _connectionString = config.GetConnectionString("M2SReportServices") ?? string.Empty;
        _logger = logger;
    }

    public async Task<List<RawMetricRow>> GetStoredDayAsync(DateTime date, CancellationToken ct = default)
    {
        var rows = new List<RawMetricRow>();

        try
        {
            // Si el día está marcado como "revisado, sin datos", no hace falta ni consultar
            // la tabla de líneas — ya sabemos que va a venir vacía.
            var (isStored, hasData) = await GetDayMarkerAsync(date, ct);
            if (isStored && !hasData) return rows;

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new SqlCommand(@"
                SELECT ShiftId, ShiftDesc, ProductGroupId, ProductDesc, ProductOrder, ProductListId,
                       CycleTimeSecs, Total, AccumulatedRate, PlannedShift, OeeShift, TotalSap
                FROM dbo.PlantMetrics_OeeHistory
                WHERE EventDate = @EventDate", conn);
            cmd.Parameters.AddWithValue("@EventDate", date.Date);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var eventDateShort = date.ToString("yyyy/MM/dd");

            while (await reader.ReadAsync(ct))
            {
                rows.Add(new RawMetricRow(
                    ReportGroup: 1,
                    GroupId: reader.GetInt32(reader.GetOrdinal("ProductGroupId")),
                    Desc: reader.GetString(reader.GetOrdinal("ProductDesc")),
                    ProductOrder: reader.GetInt32(reader.GetOrdinal("ProductOrder")),
                    ProductListId: reader.GetInt32(reader.GetOrdinal("ProductListId")),
                    CycleTimeSecs: reader.GetDouble(reader.GetOrdinal("CycleTimeSecs")),
                    PlannedForOee: reader.GetInt32(reader.GetOrdinal("PlannedShift")),
                    AccumRate: reader.GetInt32(reader.GetOrdinal("AccumulatedRate")),
                    OeeShift: reader.GetDouble(reader.GetOrdinal("OeeShift")),
                    Total: reader.GetInt32(reader.GetOrdinal("Total")),
                    TotalSap: reader.GetInt32(reader.GetOrdinal("TotalSap")),
                    Hour: "",
                    ShiftId: reader.GetInt32(reader.GetOrdinal("ShiftId")),
                    ShiftDesc: reader.GetString(reader.GetOrdinal("ShiftDesc")),
                    EventDateShort: eventDateShort
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leyendo PlantMetrics_OeeHistory para {Date}", date);
        }

        return rows;
    }

    public async Task<bool> IsDayStoredAsync(DateTime date, CancellationToken ct = default)
    {
        var (isStored, _) = await GetDayMarkerAsync(date, ct);
        return isStored;
    }

    /// <summary>Consulta la tabla "marcador" (PlantMetrics_OeeHistory_Days): ¿ya se revisó
    /// este día contra el SP? Y si sí, ¿tuvo producción o no? Esto es lo que evita volver a
    /// pegarle al SP por días vacíos (fines de semana, etc.) una y otra vez.</summary>
    private async Task<(bool IsStored, bool HasData)> GetDayMarkerAsync(DateTime date, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT HasData FROM dbo.PlantMetrics_OeeHistory_Days WHERE EventDate = @EventDate", conn);
            cmd.Parameters.AddWithValue("@EventDate", date.Date);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result == null ? (false, false) : (true, Convert.ToBoolean(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checando el marcador de {Date} en PlantMetrics_OeeHistory_Days", date);
            return (false, false);
        }
    }

    public async Task UpsertDayAsync(DateTime date, List<RawMetricRow> rawRows, CancellationToken ct = default)
    {
        // Ojo: NO hay "if (lines.Count == 0) return;" aquí a propósito. Un día sin producción
        // (fin de semana, etc.) igual se debe MARCAR como revisado — si no, se vuelve a
        // consultar al SP cada vez que alguien lo pide, que es justo lo que hacía lentas las
        // gráficas Semanal/Mensual (que casi siempre incluyen algún día vacío).
        var lines = rawRows.Where(r => r.ReportGroup == 1).ToList();

        // El SAP (Report_Group=3) viene aparte del SP — se pega a cada línea antes de guardar,
        // igual que hacen PlantMetricsService/FurnaceDetailService con datos en vivo.
        var sapByKey = rawRows
            .Where(r => r.ReportGroup == 3 && r.TotalSap > 0)
            .GroupBy(r => (r.GroupId, r.Desc, r.ShiftId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.TotalSap));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        try
        {
            await using (var del = new SqlCommand("DELETE FROM dbo.PlantMetrics_OeeHistory WHERE EventDate = @EventDate", conn, tx))
            {
                del.Parameters.AddWithValue("@EventDate", date.Date);
                await del.ExecuteNonQueryAsync(ct);
            }

            foreach (var line in lines)
            {
                var sap = sapByKey.TryGetValue((line.GroupId, line.Desc, line.ShiftId), out var s) ? s : 0;

                await using var ins = new SqlCommand(@"
                    INSERT INTO dbo.PlantMetrics_OeeHistory
                        (EventDate, ShiftId, ShiftDesc, ProductGroupId, ProductDesc, ProductOrder,
                         ProductListId, CycleTimeSecs, Total, AccumulatedRate, PlannedShift, OeeShift, TotalSap)
                    VALUES
                        (@EventDate, @ShiftId, @ShiftDesc, @ProductGroupId, @ProductDesc, @ProductOrder,
                         @ProductListId, @CycleTimeSecs, @Total, @AccumulatedRate, @PlannedShift, @OeeShift, @TotalSap)", conn, tx);

                ins.Parameters.AddWithValue("@EventDate", date.Date);
                ins.Parameters.AddWithValue("@ShiftId", line.ShiftId);
                ins.Parameters.AddWithValue("@ShiftDesc", string.IsNullOrEmpty(line.ShiftDesc) ? (object)DBNull.Value : line.ShiftDesc);
                ins.Parameters.AddWithValue("@ProductGroupId", line.GroupId);
                ins.Parameters.AddWithValue("@ProductDesc", line.Desc);
                ins.Parameters.AddWithValue("@ProductOrder", line.ProductOrder);
                ins.Parameters.AddWithValue("@ProductListId", line.ProductListId);
                ins.Parameters.AddWithValue("@CycleTimeSecs", line.CycleTimeSecs);
                ins.Parameters.AddWithValue("@Total", line.Total);
                ins.Parameters.AddWithValue("@AccumulatedRate", line.AccumRate);
                ins.Parameters.AddWithValue("@PlannedShift", line.PlannedForOee);
                ins.Parameters.AddWithValue("@OeeShift", line.OeeShift);
                ins.Parameters.AddWithValue("@TotalSap", sap);

                await ins.ExecuteNonQueryAsync(ct);
            }

            // Marca el día como "revisado" sin importar si tuvo datos o no (MERGE por si ya
            // existía el marcador de un backfill anterior).
            await using (var mark = new SqlCommand(@"
                MERGE dbo.PlantMetrics_OeeHistory_Days AS target
                USING (SELECT @EventDate AS EventDate) AS src
                ON target.EventDate = src.EventDate
                WHEN MATCHED THEN UPDATE SET HasData = @HasData, CapturedAt = GETDATE()
                WHEN NOT MATCHED THEN INSERT (EventDate, HasData, CapturedAt) VALUES (@EventDate, @HasData, GETDATE());", conn, tx))
            {
                mark.Parameters.AddWithValue("@EventDate", date.Date);
                mark.Parameters.AddWithValue("@HasData", lines.Count > 0);
                await mark.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            _logger.LogError(ex, "Error guardando {Date} en PlantMetrics_OeeHistory", date);
        }
    }

    public async Task<(DateTime? OldestDate, int ConsecutiveEmptyDays, bool IsComplete)> GetBackfillStateAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT TOP 1 OldestDateBackfilled, ConsecutiveEmptyDays, IsComplete FROM dbo.PlantMetrics_OeeHistory_BackfillState ORDER BY Id", conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var oldest = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
                return (oldest, reader.GetInt32(1), reader.GetBoolean(2));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error leyendo el progreso del backfill de OEE histórico");
        }

        return (null, 0, false);
    }

    public async Task UpdateBackfillProgressAsync(DateTime? oldestDate, int consecutiveEmptyDays, bool isComplete, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
                UPDATE dbo.PlantMetrics_OeeHistory_BackfillState
                SET OldestDateBackfilled = @Oldest, ConsecutiveEmptyDays = @Empty, IsComplete = @Complete, UpdatedAt = GETDATE()", conn);
            cmd.Parameters.AddWithValue("@Oldest", (object?)oldestDate?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Empty", consecutiveEmptyDays);
            cmd.Parameters.AddWithValue("@Complete", isComplete);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error actualizando el progreso del backfill de OEE histórico");
        }
    }
}
