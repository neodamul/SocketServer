using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketControl.Model;
using SocketDashboard.Model;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class DashboardServerServiceTests
{
    [TestMethod]
    public void GetStatusReturnsRunningServerStatusTest()
    {
        using DashboardServerService service = new(0);

        DashboardServerStatus status = service.GetStatus();

        Assert.IsTrue(status.StartSucceeded);
        Assert.IsTrue(status.Server.IsSocketInitialized);
        Assert.IsTrue(status.Server.IsBound);
        Assert.IsTrue(status.Server.IsListening);
        Assert.IsTrue(status.Server.IsAcceptLoopRunning);
        Assert.AreEqual(TcpServer.DefaultMaxConnections, status.Server.MaxConnections);
        Assert.AreEqual(TcpServer.DefaultPendingAcceptCount, status.Server.PendingAcceptCount);
        Assert.AreEqual(TcpServer.DefaultIdleTimeoutSeconds, status.Server.IdleTimeoutSeconds);
        Assert.AreEqual(0, status.Server.TotalRejectedClients);
        Assert.AreEqual(0, status.Server.TotalIdleTimeoutClients);
        Assert.AreEqual(SocketFactory.ListenBacklog, status.Server.ListenBacklog);
        Assert.AreEqual(SocketFactory.NoDelay, status.Server.NoDelay);
        Assert.AreEqual(SocketMessageFrame.MaxPayloadLength, status.Server.MaxPayloadLength);
        Assert.IsTrue(status.Server.SocketAsyncEventArgsTotalCreatedCount >= SocketAsyncEventArgsFactory.InitialPoolSize);
        Assert.IsTrue(status.Server.SocketAsyncEventArgsHighWatermarkInUseCount >= 0);
        Assert.IsTrue(status.Server.Port > 0);
        Assert.AreEqual(1, status.Cluster.ServerCount);
    }

    [TestMethod]
    public void GetStatusUsesControlServerClusterStatusWhenAvailableTest()
    {
        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-dashboard",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0
            }
        });
        Assert.IsTrue(controlServer.Start());
        controlServer.Registry.Upsert(new ServerRegisterRequest
        {
            ClusterId = "socket-cluster-1",
            ServerId = 7,
            InstanceId = "server-dashboard",
            Name = "server-dashboard",
            Host = "127.0.0.1",
            Port = 5107,
            PortRangeStart = 5100,
            PortRangeEnd = 5199,
            MaxConnections = 123,
            PendingAcceptCount = 10,
            IdleTimeoutSeconds = 90
        }, "control-dashboard");

        using DashboardServerService service = new(
            0,
            new EndpointConfig { Host = "127.0.0.1", Port = controlServer.Port });

        DashboardServerStatus status = service.GetStatus();

        Assert.AreEqual(1, status.Cluster.ServerCount);
        Assert.AreEqual(123, status.Cluster.TotalMaxConnections);
        Assert.AreEqual("server-dashboard", status.Cluster.Servers.First().InstanceId);
    }
}
