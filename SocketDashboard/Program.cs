using SocketDashboard.Model;
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SocketCommon.Logging;

LogConfigurator.Configure();
SocketLogger logger = SocketLogManager.GetLogger(typeof(Program));

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

if (String.IsNullOrEmpty(builder.Configuration["urls"]) &&
    String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
}

builder.Services.AddSingleton<DashboardServerService>();

WebApplication app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/server/status", (DashboardServerService serverService) => serverService.GetStatus());
app.MapGet("/health/live", (DashboardServerService serverService) => serverService.GetLiveness());
app.MapGet("/health/ready", (DashboardServerService serverService) => serverService.GetReadiness());
app.MapGet("/metrics", (DashboardServerService serverService) => serverService.GetMetrics());

logger.Info("SocketDashboard starting.");
app.Run();
