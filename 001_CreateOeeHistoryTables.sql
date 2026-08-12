-- ============================================================================
-- PlantMetrics_OeeHistory
-- Guarda, por (día, turno, línea), el resultado YA CALCULADO del SP
-- (Total, Plan, OEE, SAP) para turnos que ya terminaron. Una vez que un día
-- está aquí, las gráficas históricas leen de esta tabla en vez de volver a
-- pegarle al SP — por eso el rendimiento mejora tanto.
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PlantMetrics_OeeHistory')
BEGIN
    CREATE TABLE [dbo].[PlantMetrics_OeeHistory] (
        [Id]              INT IDENTITY(1,1) PRIMARY KEY,
        [EventDate]       DATE            NOT NULL,
        [ShiftId]         TINYINT         NOT NULL,
        [ShiftDesc]       NVARCHAR(100)   NOT NULL,
        [ProductGroupId]  INT             NOT NULL,
        [ProductDesc]     NVARCHAR(200)   NOT NULL,
        [ProductOrder]    INT             NOT NULL DEFAULT 0,
        [ProductListId]   INT             NOT NULL DEFAULT 0,
        [CycleTimeSecs]   FLOAT           NOT NULL DEFAULT 0,
        [Total]           INT             NOT NULL DEFAULT 0,
        [AccumulatedRate] INT             NOT NULL DEFAULT 0,
        [PlannedShift]    INT             NOT NULL DEFAULT 0,
        [OeeShift]        FLOAT           NOT NULL DEFAULT 0,
        [TotalSap]        INT             NOT NULL DEFAULT 0,
        [CapturedAt]      DATETIME        NOT NULL DEFAULT GETDATE()
    );

    CREATE UNIQUE INDEX UX_PlantMetrics_OeeHistory_Key
        ON [dbo].[PlantMetrics_OeeHistory] ([EventDate], [ShiftId], [ProductGroupId], [ProductDesc]);

    CREATE INDEX IX_PlantMetrics_OeeHistory_Date
        ON [dbo].[PlantMetrics_OeeHistory] ([EventDate]);
END
GO

-- ============================================================================
-- PlantMetrics_OeeHistory_BackfillState
-- Una sola fila que trae el progreso del "relleno" del historial viejo, para
-- que si la app se reinicia a medio backfill, siga donde se quedó en vez de
-- volver a empezar desde hoy.
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PlantMetrics_OeeHistory_BackfillState')
BEGIN
    CREATE TABLE [dbo].[PlantMetrics_OeeHistory_BackfillState] (
        [Id]                    INT IDENTITY(1,1) PRIMARY KEY,
        [OldestDateBackfilled]  DATE     NULL,
        [ConsecutiveEmptyDays]  INT      NOT NULL DEFAULT 0,
        [IsComplete]            BIT      NOT NULL DEFAULT 0,
        [UpdatedAt]             DATETIME NOT NULL DEFAULT GETDATE()
    );

    INSERT INTO [dbo].[PlantMetrics_OeeHistory_BackfillState]
        ([OldestDateBackfilled], [ConsecutiveEmptyDays], [IsComplete])
    VALUES (NULL, 0, 0);
END
GO
