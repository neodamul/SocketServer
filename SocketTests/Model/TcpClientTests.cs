using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    private const int TestPort = 5001;
    private Mutex? testPortMutex;

    [TestInitialize]
    public void Initialize()
    {
        this.testPortMutex = new Mutex(false, "SocketServer.TestPort.5001");
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

    private static Socket CreateListener()
    {
        Socket listener = SocketFactory.CreateTcpSocket();
        listener.Bind(new IPEndPoint(IPAddress.Loopback, TestPort));
        listener.Listen(SocketFactory.ListenBacklog);
        return listener;
    }

    private static async Task<SecureSocketConnection> AcceptSecureAsync(Socket listener)
    {
        Socket accepted = await listener.AcceptAsync();
        return await SecureSocketConnection.AuthenticateServerAsync(accepted, "SocketTests");
    }
}
