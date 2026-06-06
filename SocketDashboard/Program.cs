using SocketDashboard.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SocketCommon;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketCommon.Model;

LogConfigurator.Configure();
SocketLogger logger = SocketLogManager.GetLogger(typeof(Program));

string outputWebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Directory.Exists(outputWebRootPath) ? outputWebRootPath : "wwwroot"
});

if (String.IsNullOrEmpty(builder.Configuration["urls"]) &&
    String.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://127.0.0.1:10050");
}

IReadOnlyCollection<EndpointConfig> controlEndpoints = ReadControlEndpoints(builder);
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

builder.Services.AddSingleton(_ => new DashboardServerService(0, controlEndpoints));

WebApplication app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/server/status", async (DashboardServerService serverService) => await serverService.GetStatusAsync());
app.MapGet("/health/live", (DashboardServerService serverService) => serverService.GetLiveness());
app.MapGet("/health/ready", (DashboardServerService serverService) => serverService.GetReadiness());
app.MapGet("/metrics", async (DashboardServerService serverService) => await serverService.GetMetricsAsync());

logger.Info("SocketDashboard starting.");
app.Run();

static IReadOnlyCollection<EndpointConfig> ReadControlEndpoints(WebApplicationBuilder builder)
{
    EndpointConfig[] endpoints = builder.Configuration
        .GetSection("dashboard:controlServers")
        .GetChildren()
        .Select(section => new EndpointConfig
        {
            Host = section["host"] ?? Constants.LocalHostIpAddress,
            Port = Int32.TryParse(section["port"], out int port) ? port : Constants.LocalHostPort
        })
        .Where(endpoint => endpoint.Port > 0)
        .GroupBy(endpoint => $"{endpoint.Host}:{endpoint.Port}", StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();
    if (endpoints.Length > 0)
    {
        return endpoints;
    }

    return new[]
    {
        new EndpointConfig
        {
            Host = builder.Configuration["dashboard:controlServer:host"] ?? Constants.LocalHostIpAddress,
            Port = Int32.TryParse(builder.Configuration["dashboard:controlServer:port"], out int controlPort)
                ? controlPort
                : Constants.LocalHostPort
        }
    };
}
