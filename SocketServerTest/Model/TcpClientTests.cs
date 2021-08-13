using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketServer.Model;

namespace SocketServerTest;
[TestClass]
public class TcpClientTests
{
    [TestMethod]
    public void ClientInitializeTest()
    {
        TcpClient client = new();
        client.Initialize();
    }

    [TestMethod]
    public void ClientConnectTest()
    {
        TcpClient client = new();
        client.Connect();
    }

    [TestMethod]
    public void ClientDisconnectTest()
    {
        TcpClient client = new();
        client.Disconnect();
    }

    [TestMethod]
    public void ClientIsConnectedTest()
    {
        TcpClient client = new();
        client.IsConnected();
    }

    [TestMethod]
    public void ClientSetAddressTest()
    {
        TcpClient client = new();
        client.SetIpAddress("127.0.0.1");
        Assert.AreEqual(client.GetIpAddress(), "127.0.0.1");
    }

    [TestMethod]
    public void ClientSetPortTest()
    {
        TcpClient client = new();
        client.SetPort(0);
        Assert.AreEqual(client.GetPort(), 0);
    }
}