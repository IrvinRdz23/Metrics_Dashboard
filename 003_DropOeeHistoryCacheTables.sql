-- Corre esto para quitar todo lo que agregamos para el backfill/cache de OeeHistory.
-- No afecta ninguna otra tabla ni al SP original.

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PlantMetrics_OeeHistory_Days')
    DROP TABLE [dbo].[PlantMetrics_OeeHistory_Days];

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PlantMetrics_OeeHistory_BackfillState')
    DROP TABLE [dbo].[PlantMetrics_OeeHistory_BackfillState];

IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'PlantMetrics_OeeHistory')
    DROP TABLE [dbo].[PlantMetrics_OeeHistory];
GO
