using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SocketClientTcpClient = SocketClient.Model.TcpClient;

namespace SocketClientTest.Model;
[TestClass]
public class TcpClientTests
{
    private const int TestPort = 5001;

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
        Task<Socket> acceptTask = listener.AcceptAsync();

        SocketClientTcpClient client = new(1, "testClient", "127.0.0.1", TestPort);
        Assert.IsTrue(client.Connect());
        using Socket accepted = await acceptTask;
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

    private static Socket CreateListener()
    {
        Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, TestPort));
        listener.Listen(10);
        return listener;
    }
}
