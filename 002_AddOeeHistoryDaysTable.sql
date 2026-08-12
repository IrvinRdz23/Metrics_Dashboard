-- ============================================================================
-- PlantMetrics_OeeHistory_Days
-- Marca CUALQUIER día ya revisado contra el SP, tenga o no producción. Sin esto,
-- los días vacíos (fines de semana, etc.) nunca quedaban "cacheados" y se volvían
-- a consultar al SP cada vez — eso era lo que hacía lentas las gráficas Semanal y
-- Mensual, que naturalmente abarcan varios días sin producción.
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PlantMetrics_OeeHistory_Days')
BEGIN
    CREATE TABLE [dbo].[PlantMetrics_OeeHistory_Days] (
        [EventDate]  DATE     NOT NULL PRIMARY KEY,
        [HasData]    BIT      NOT NULL DEFAULT 0,
        [CapturedAt] DATETIME NOT NULL DEFAULT GETDATE()
    );

    -- Migración: si ya tenías días guardados con producción de antes de este cambio,
    -- márcalos aquí también para no perder el avance que ya llevabas.
    INSERT INTO [dbo].[PlantMetrics_OeeHistory_Days] (EventDate, HasData)
    SELECT DISTINCT EventDate, 1
    FROM [dbo].[PlantMetrics_OeeHistory];
END
GO
