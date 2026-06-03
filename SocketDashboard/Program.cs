using SocketDashboard.Model;
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SocketCommon;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketCommon.Model;

LogConfigurator.Configure();
SocketLogger logger = SocketLogManager.GetLogger(typeof(Program));

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

if (String.IsNullOrEmpty(builder.Configuration["urls"]) &&
    String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
}

EndpointConfig controlEndpoint = new()
{
    Host = builder.Configuration["dashboard:controlServer:host"] ?? Constants.LocalHostIpAddress,
    Port = Int32.TryParse(builder.Configuration["dashboard:controlServer:port"], out int controlPort)
        ? controlPort
        : Constants.LocalHostPort
};
SocketFactory.Configure(new SocketOperationConfig
{
    ConnectTimeoutSeconds = Int32.TryParse(builder.Configuration["dashboard:socketOptions:connectTimeoutSeconds"], out int connectTimeoutSeconds)
        ? connectTimeoutSeconds
        : SocketFactory.DefaultOperationTimeoutSeconds,
    ReadTimeoutSeconds = Int32.TryParse(builder.Configuration["dashboard:socketOptions:readTimeoutSeconds"], out int readTimeoutSeconds)
        ? readTimeoutSeconds
        : SocketFactory.DefaultOperationTimeoutSeconds,
    WriteTimeoutSeconds = Int32.TryParse(builder.Configuration["dashboard:socketOptions:writeTimeoutSeconds"], out int writeTimeoutSeconds)
        ? writeTimeoutSeconds
        : SocketFactory.DefaultOperationTimeoutSeconds
});

builder.Services.AddSingleton(_ => new DashboardServerService(0, controlEndpoint));

WebApplication app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/server/status", (DashboardServerService serverService) => serverService.GetStatus());
app.MapGet("/health/live", (DashboardServerService serverService) => serverService.GetLiveness());
app.MapGet("/health/ready", (DashboardServerService serverService) => serverService.GetReadiness());
app.MapGet("/metrics", (DashboardServerService serverService) => serverService.GetMetrics());

logger.Info("SocketDashboard starting.");
app.Run();
