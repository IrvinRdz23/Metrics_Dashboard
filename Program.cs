using Metrics_Dashboard.Hubs;
using Metrics_Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();

// Un solo lector del SP (compartido por el dashboard general, los 6 de horno y las 53 de línea).
builder.Services.AddScoped<IMetricsRawDataService, MetricsRawDataService>();
builder.Services.AddScoped<IPlantMetricsService, PlantMetricsService>();
builder.Services.AddScoped<IFurnaceDetailService, FurnaceDetailService>();
builder.Services.AddScoped<ILineDetailService, LineDetailService>();
builder.Services.AddScoped<IOeeHistoryService, OeeHistoryService>();
builder.Services.AddSingleton<IBreakScheduleService, BreakScheduleService>();
builder.Services.AddSingleton<IHeijunkaService, HeijunkaService>();

builder.Services.AddHostedService<MetricsBroadcastService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Dashboard/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.MapHub<MetricsHub>("/hubs/metrics");

app.Run();
