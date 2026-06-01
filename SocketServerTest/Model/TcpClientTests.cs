using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketServer.Model;

namespace SocketServerTest.Model;
[TestClass]
public class TcpClientTests
{
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
        TcpServer server = new(1, "testServer");
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
        client.SetPort(0);
        Assert.AreEqual(0, client.GetPort());
    }
}
