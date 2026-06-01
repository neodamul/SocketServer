using System;
using SocketCommon;
using SocketCommon.Logging;
using SocketServer.Model;

namespace SocketDashboard.Model;

public class DashboardServerService : IDisposable
{
    private static readonly SocketLogger Logger = SocketLogManager.GetLogger<DashboardServerService>();

    private readonly TcpServer server;
    private bool disposedValue;

    public DashboardServerService()
        : this(Constants.LocalHostPort)
    {
    }

    public DashboardServerService(int port)
    {
        this.StartedAt = DateTimeOffset.UtcNow;
        this.server = new TcpServer(1, "dashboardServer", Constants.LocalHostIpAddress, port);
        this.StartSucceeded = this.server.Start() && this.server.StartClientAcceptLoop();
        Logger.Info($"Dashboard server service started. port={port}, success={this.StartSucceeded}");
    }

    public DateTimeOffset StartedAt { get; }

    public bool StartSucceeded { get; }

    public DashboardServerStatus GetStatus()
    {
        return new DashboardServerStatus
        {
            DashboardStartedAt = this.StartedAt,
            StartSucceeded = this.StartSucceeded,
            Server = this.server.GetStatus()
        };
    }

    public void Dispose()
    {
        if (!this.disposedValue)
        {
            this.server.Dispose();
            Logger.Info("Dashboard server service disposed.");
            this.disposedValue = true;
            GC.SuppressFinalize(this);
        }
    }
}

public class DashboardServerStatus
{
    public DateTimeOffset DashboardStartedAt { get; init; }

    public bool StartSucceeded { get; init; }

    public TcpServerStatus Server { get; init; } = new();
}
