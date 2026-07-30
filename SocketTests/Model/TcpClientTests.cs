using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Model;
using SocketClientTcpClient = SocketClient.Model.TcpClient;

namespace SocketTests.Model;
[TestClass]
public class TcpClientTests
{
    private const int TestPort = 25001;
    private Mutex? testPortMutex;

    [TestInitialize]
    public void Initialize()
    {
        this.testPortMutex = new Mutex(false, "SocketServer.TestPort.25001");
        this.testPortMutex.WaitOne();
    }

    [TestCleanup]
    public void Cleanup()
    {
        this.testPortMutex?.ReleaseMutex();
        this.testPortMutex?.Dispose();
    }

    [TestMethod]
    public void ClientInitializeTest()
    {
        SocketClientTcpClient client = new();
        client.Initialize();
        Assert.IsFalse(client.IsConnected());
    }

    [TestMethod]
    public async Task ClientConnectTest()
    {
        using Socket listener = CreateListener();
        Task<SecureSocketConnection> acceptTask = AcceptSecureAsync(listener);

        SocketClientTcpClient client = new(1, "testClient", "127.0.0.1", TestPort);
        Assert.IsTrue(client.Connect());
        using SecureSocketConnection accepted = await acceptTask;
        Assert.IsTrue(client.IsConnected());

        client.Disconnect();
    }

    [TestMethod]
    public void ClientDisconnectTest()
    {
        SocketClientTcpClient client = new();
        Assert.IsTrue(client.Disconnect());
        Assert.IsFalse(client.IsConnected());
    }

    [TestMethod]
    public void ClientIsConnectedTest()
    {
        SocketClientTcpClient client = new();
        Assert.IsFalse(client.IsConnected());
    }

    [TestMethod]
    public void ClientSetAddressTest()
    {
        SocketClientTcpClient client = new();
        client.SetIpAddress("127.0.0.1");
        Assert.AreEqual("127.0.0.1", client.GetIpAddress());
    }

    [TestMethod]
    public void ClientSetPortTest()
    {
        SocketClientTcpClient client = new();
        client.SetPort(TestPort);
        Assert.AreEqual(TestPort, client.GetPort());
    }

    [TestMethod]
    public async Task ClientHelloWorldRequestTest()
    {
        using Socket listener = CreateListener();
        Task<SecureSocketConnection> acceptTask = AcceptSecureAsync(listener);

        SocketClientTcpClient client = new(1, "testClient", "127.0.0.1", TestPort);
        Assert.IsTrue(client.Connect());
        using SecureSocketConnection accepted = await acceptTask;

        Assert.IsTrue(await client.SendHelloWorldRequestAsync());
        (bool requestReceived, HelloWorldRequest request) = await HelloWorldProtocol.TryReceiveRequestAsync(accepted);
        Assert.IsTrue(requestReceived);
        Assert.IsNotNull(request);
        Assert.AreEqual((uint)1, request.ClientId);

        Assert.IsTrue(await HelloWorldProtocol.SendAsync(accepted, HelloWorldProtocol.CreateResponse(request.ClientId)));
        (bool responseReceived, HelloWorldResponse response) = await client.TryReceiveHelloWorldResponseAsync();
        Assert.IsTrue(responseReceived);
        Assert.AreEqual((uint)1, response.ClientId);
        Assert.AreEqual("Hello, World!", response.Message);

        client.Disconnect();
    }

    [TestMethod]
    public async Task ClientConnectViaControlServerRetriesSelectedRouteConnectTest()
    {
        int serverPort = GetAvailablePort();
        using Socket controlListener = CreateListener(0);
        using Socket serverListener = CreateListener(serverPort);
        int controlPort = ((IPEndPoint)controlListener.LocalEndPoint!).Port;
        int routeRequestCount = 0;
        int serverAcceptCount = 0;

        Task controlTask = Task.Run(async () =>
        {
            using SecureSocketConnection controlConnection = await AcceptSecureAsync(controlListener);
            (bool received, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(controlConnection);
            Assert.IsTrue(received);
            Assert.IsTrue(ControlProtocol.TryDecode(frame, ControlMessageIds.RouteRequest, out RouteRequest request));
            Interlocked.Increment(ref routeRequestCount);

            Assert.IsTrue(await ControlProtocol.SendAsync(
                controlConnection,
                request.ClientId,
                ControlMessageIds.RouteResponse,
                new RouteResponse
                {
                    Success = true,
                    Host = "127.0.0.1",
                    Port = serverPort,
                    InstanceId = "socket-retry-test",
                    ReservationId = "reservation-retry-test",
                    ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60)
                }));
        });

        Task<SecureSocketConnection> serverAcceptTask = Task.Run(async () =>
        {
            using Socket firstAccepted = await serverListener.AcceptAsync();
            Interlocked.Increment(ref serverAcceptCount);
            firstAccepted.Dispose();

            Socket secondAccepted = await serverListener.AcceptAsync();
            Interlocked.Increment(ref serverAcceptCount);
            return await SecureSocketConnection.AuthenticateServerAsync(secondAccepted, "SocketTests");
        });

        SocketClientTcpClient client = new(11, "route-retry-client");
        Assert.IsTrue(await client.ConnectViaControlServerAsync("127.0.0.1", controlPort));
        using SecureSocketConnection accepted = await serverAcceptTask;

        Assert.IsTrue(client.IsConnected());
        Assert.AreEqual(1, routeRequestCount);
        Assert.AreEqual(2, serverAcceptCount);

        client.Disconnect();
        await controlTask;
    }

    private static Socket CreateListener(int port = TestPort)
    {
        Socket listener = SocketFactory.CreateTcpSocket();
        listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
        listener.Listen(SocketFactory.ListenBacklog);
        return listener;
    }

    private static async Task<SecureSocketConnection> AcceptSecureAsync(Socket listener)
    {
        Socket accepted = await listener.AcceptAsync();
        return await SecureSocketConnection.AuthenticateServerAsync(accepted, "SocketTests");
    }

    private static int GetAvailablePort()
    {
        using Socket socket = SocketFactory.CreateTcpSocket();
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
