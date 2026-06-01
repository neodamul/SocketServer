using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketServer.Model;

namespace SocketServerTest.Model;
[TestClass]
public class TcpClientTests
{
    private const int TestPort = 5001;

    [TestMethod]
    public void ClientInitializeTest()
    {
        TcpClient client = new();
        client.Initialize();
        Assert.IsFalse(client.IsConnected());
    }

    [TestMethod]
    public void ClientConnectTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Start());

        TcpClient client = new(1, "testClient", server.GetIpAddress(), server.GetPort());
        Assert.IsTrue(client.Connect());
        Assert.IsTrue(client.IsConnected());

        client.Disconnect();
        server.End();
    }

    [TestMethod]
    public void ClientDisconnectTest()
    {
        TcpClient client = new();
        Assert.IsTrue(client.Disconnect());
        Assert.IsFalse(client.IsConnected());
    }

    [TestMethod]
    public void ClientIsConnectedTest()
    {
        TcpClient client = new();
        Assert.IsFalse(client.IsConnected());
    }

    [TestMethod]
    public void ClientSetAddressTest()
    {
        TcpClient client = new();
        client.SetIpAddress("127.0.0.1");
        Assert.AreEqual("127.0.0.1", client.GetIpAddress());
    }

    [TestMethod]
    public void ClientSetPortTest()
    {
        TcpClient client = new();
        client.SetPort(TestPort);
        Assert.AreEqual(TestPort, client.GetPort());
    }
}
