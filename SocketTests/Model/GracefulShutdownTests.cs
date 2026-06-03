using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketControl.Model;
using SocketServer.Model;
using ModelTcpClient = SocketClient.Model.TcpClient;

namespace SocketTests.Model;

[TestClass]
public class GracefulShutdownTests
{
    [TestMethod]
    public async Task ControlServerStopAsyncClosesListenerTest()
    {
        SocketSecurityConfig security = CreateSecurity();
        SecureSocketConnection.Configure(security);
        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            Security = security,
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-shutdown",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0
            }
        });

        Assert.IsTrue(controlServer.Start());
        int port = controlServer.Port;
        Assert.IsTrue(await CanConnectAsync(port));

        await controlServer.StopAsync(TimeSpan.FromSeconds(2));

        Assert.IsFalse(await CanConnectAsync(port));
    }

    [TestMethod]
    public async Task SocketServerEndAsyncClosesClientsAndListenerTest()
    {
        SocketSecurityConfig security = CreateSecurity();
        SecureSocketConnection.Configure(security);
        using TcpServer server = CreateStartedServer();
        using ModelTcpClient client = new(1001, "shutdown-client", "127.0.0.1", server.GetPort());

        Assert.IsTrue(client.Connect());
        Assert.IsTrue(await client.RegisterClientAsync());
        await WaitUntilAsync(() => server.GetConnectedClientCount() == 1);

        Assert.IsTrue(await server.EndAsync(TimeSpan.FromSeconds(2)));

        TcpServerStatus status = server.GetStatus();
        Assert.IsFalse(status.IsListening);
        Assert.IsFalse(status.IsAcceptLoopRunning);
        Assert.AreEqual(0, server.GetConnectedClientCount());
        Assert.IsFalse(await CanConnectAsync(server.GetPort()));
    }

    [TestMethod]
    public async Task SocketClientDisconnectAsyncStopsHealthCheckAndClosesConnectionTest()
    {
        SocketSecurityConfig security = CreateSecurity();
        SecureSocketConnection.Configure(security);
        using TcpServer server = CreateStartedServer();
        using ModelTcpClient client = new(1002, "shutdown-health-client", "127.0.0.1", server.GetPort());

        Assert.IsTrue(client.Connect());
        Assert.IsTrue(client.StartHealthCheckLoop(TimeSpan.FromMilliseconds(100)));

        Assert.IsTrue(await client.DisconnectAsync(TimeSpan.FromSeconds(2)));

        Assert.IsFalse(client.IsConnected());
        Assert.IsFalse(await client.SendHealthCheckAsync());
        Assert.IsTrue(await server.EndAsync(TimeSpan.FromSeconds(2)));
    }

    private static TcpServer CreateStartedServer()
    {
        TcpServer server = new(
            100,
            "shutdown-server",
            "127.0.0.1",
            0,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(30),
            instanceId: "shutdown-server");
        Assert.IsTrue(server.BindInPortRange(0, 0));
        Assert.IsTrue(server.Listen());
        Assert.IsTrue(server.StartClientAcceptLoop());
        return server;
    }

    private static SocketSecurityConfig CreateSecurity()
    {
        return new SocketSecurityConfig
        {
            TransportMode = "Tls",
            TlsProtocol = "Auto",
            RequireTls13 = false,
            AuthenticationTimeoutMilliseconds = 5000
        };
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        using Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Timed out waiting for condition.");
    }
}
