# Planta 4 — Dashboard de métricas (proyecto aparte de Planta 1)

C# .NET 8 MVC + SignalR + ADO.NET + Chart.js (todo local, sin CDN).

## Qué cambia respecto a Planta 1
| Tema | Planta 1 | Planta 4 |
|---|---|---|
| Plant_List_ID | 1 | **3** (`appsettings.json` → `PlantMetrics:PlantListId`) |
| "Hornos" | Furnace 1-5 + Clam Shells + Tube Mills | **Grupos**: 8 Condenser, 9 Battery Cooling Plate, 10 Heater Core, 11 Residential (`Models/Models.cs` → `ProductGroupCatalog`) |
| Layout | 3x2 (hornos + tendencia) | **2x2** + pantalla de tendencias con 4 gráficas, una por grupo (flechas fantasma / teclado ← →) |
| SAP | CB/BE/Clam Shell excluidos | **Todas** las líneas cuentan para SAP; la tendencia de cada grupo lleva su línea de SAP/hora |
| Heijunka | Siempre activo | **Apagado** (`HeijunkaEnabled: false`). Con `true` aplican las mismas reglas que Planta 1 |
| Rutas | `/Furnace{n}`, `/Line/{id}` | `/Group/{groupId}`, `/Line/{id}`, `/Historical` |

## Rutas
- `/` dashboard en vivo (SignalR en `/hubs/metrics`)
- `/Historical?date=2026-08-13&shiftId=1` histórico por turno (sin fecha: hoy, último turno cerrado)
- `/Group/{8|9|10|11}` detalle del grupo — acepta `?date=&shiftId=`
- `/Line/{Product_List_ID}` TV de la línea — acepta `?date=&shiftId=`
- `/Group/{id}/Data` y `/Line/{id}/Data` devuelven el JSON con el que las vistas se refrescan cada 10 s

## Para encender Heijunka en Planta 4
Pon `"HeijunkaEnabled": true`. Se consulta `Product_List` (Product_List_ID → Product_List_Group_ID, filtrado por `Plant_List_ID`) y `Heijunka_Plan_List` (fila de la semana, columna `{Día}_Shift_{N}`).
Si no hay fila de esa semana o `Heijunka_Enabled = 0` para el grupo, esa línea cae al criterio de siempre (`Planned_Shift_for_OEE > 0`).
Con Heijunka activo: línea planeada sin producción = barra en 0 % y baja el OEE; línea NO planeada que produjo = no cuenta, se ve en rojo (etiqueta "NO PLANEADA") solo en detalle/modal.

## Otros ajustes (`PlantMetrics`)
- `RefreshSeconds` (20): cada cuánto se empuja el snapshot por SignalR (sin clientes conectados no se consulta el SP).
- `LiveCacheSeconds` (5): caché del SP del día en curso, para que muchas TVs no saturen la base.
- `Breaks`: opcional, lista `{ "Start": "09:00", "End": "09:15" }`; solo afina el tiempo de ciclo real por hora.
- `UseDemoData: true`: datos de mentira para probar sin SQL Server.

## Logos
Copia de Planta 1 a `wwwroot/images/`: `m2s.png` y `MAHLE-Logo.png`.

## Publicar
`dotnet publish -c Release` → carpeta publish al sitio de IIS (Hosting Bundle .NET 8). Cambia el connection string en `appsettings.json`.
