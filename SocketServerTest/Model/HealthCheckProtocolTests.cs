using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketServer.Model;

namespace SocketServerTest.Model;

[TestClass]
public class HealthCheckProtocolTests
{
    private const int TestPort = 5001;

    [TestMethod]
    public void KeepAliveIntervalTest()
    {
        Assert.AreEqual(30, HealthCheckProtocol.KeepAliveIntervalSeconds);
    }

    [TestMethod]
    public void EncodePingTest()
    {
        byte[] bytes = HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePing());

        Assert.AreEqual("HEALTHCHECK/1 PING\n", Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    public void EncodePongTest()
    {
        byte[] bytes = HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePong());

        Assert.AreEqual("HEALTHCHECK/1 PONG OK\n", Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    public void DecodePingTest()
    {
        bool result = HealthCheckProtocol.TryDecode("HEALTHCHECK/1 PING\n", out HealthCheckMessage message);

        Assert.IsTrue(result);
        Assert.AreEqual(HealthCheckMessageType.Ping, message.Type);
        Assert.AreEqual("", message.Status);
    }

    [TestMethod]
    public void DecodePongTest()
    {
        bool result = HealthCheckProtocol.TryDecode("HEALTHCHECK/1 PONG OK\n", out HealthCheckMessage message);

        Assert.IsTrue(result);
        Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
        Assert.AreEqual("OK", message.Status);
    }

    [TestMethod]
    public void DecodeInvalidMessageTest()
    {
        bool result = HealthCheckProtocol.TryDecode("UNKNOWN\n", out HealthCheckMessage message);

        Assert.IsFalse(result);
        Assert.IsNull(message);
    }

    [TestMethod]
    public async Task SendAndReceivePingTest()
    {
        using Socket listener = CreateListener();
        Task<Socket> acceptTask = listener.AcceptAsync();

        using Socket client = CreateClient();
        client.Connect(IPAddress.Loopback, TestPort);
        using Socket accepted = await acceptTask;

        Assert.IsTrue(HealthCheckProtocol.Send(client, HealthCheckProtocol.CreatePing()));
        Assert.IsTrue(HealthCheckProtocol.TryReceive(accepted, out HealthCheckMessage message));
        Assert.AreEqual(HealthCheckMessageType.Ping, message.Type);
    }

    [TestMethod]
    public async Task SendAndReceivePongTest()
    {
        using Socket listener = CreateListener();
        Task<Socket> acceptTask = listener.AcceptAsync();

        using Socket client = CreateClient();
        client.Connect(IPAddress.Loopback, TestPort);
        using Socket accepted = await acceptTask;

        Assert.IsTrue(HealthCheckProtocol.Send(accepted, HealthCheckProtocol.CreatePong()));
        Assert.IsTrue(HealthCheckProtocol.TryReceive(client, out HealthCheckMessage message));
        Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
        Assert.AreEqual("OK", message.Status);
    }

    private static Socket CreateListener()
    {
        Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, TestPort));
        listener.Listen(10);
        return listener;
    }

    private static Socket CreateClient()
    {
        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        return client;
    }
}
