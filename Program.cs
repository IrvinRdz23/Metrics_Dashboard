using Metrics_Dashboard.Hubs;
using Metrics_Dashboard.Models;
using Metrics_Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PlantOptions>(builder.Configuration.GetSection("PlantMetrics"));

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddMemoryCache();

builder.Services.AddSingleton<IMetricsRawDataService, MetricsRawDataService>();
builder.Services.AddSingleton<IHeijunkaService, HeijunkaService>();
builder.Services.AddSingleton<IBreakScheduleService, BreakScheduleService>();
builder.Services.AddSingleton<IPlantMetricsService, PlantMetricsService>();
builder.Services.AddSingleton<IGroupDetailService, GroupDetailService>();
builder.Services.AddSingleton<ILineDetailService, LineDetailService>();
builder.Services.AddHostedService<MetricsBroadcastService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapGet("/Error", () => Results.Text("Ocurrió un error al procesar la solicitud. Revisa el log del servidor.", "text/plain; charset=utf-8", System.Text.Encoding.UTF8, 500));

app.MapHub<MetricsHub>("/hubs/metrics");
app.MapControllers();

app.Run();
