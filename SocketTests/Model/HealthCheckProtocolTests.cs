using System.Buffers.Binary;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketCommon.Model;

namespace SocketTests.Model;

[TestClass]
public class HealthCheckProtocolTests
{
    private const int TestPort = 5001;
    private const uint TestClientId = 7;
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
    public void KeepAliveIntervalTest()
    {
        Assert.AreEqual(30, HealthCheckProtocol.KeepAliveIntervalSeconds);
    }

    [TestMethod]
    public void EncodePingTest()
    {
        byte[] bytes = HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePing(TestClientId));

        Assert.IsTrue(bytes.Length > SocketMessageFrame.HeaderLength);
        Assert.AreEqual(TestClientId, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)));
        Assert.AreEqual(HealthCheckProtocol.PingMessageId, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)));
        Assert.IsTrue(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) > 0);
    }

    [TestMethod]
    public void EncodePongTest()
    {
        byte[] bytes = HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePong(TestClientId));

        Assert.IsTrue(bytes.Length > SocketMessageFrame.HeaderLength);
        Assert.AreEqual(TestClientId, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)));
        Assert.AreEqual(HealthCheckProtocol.PongMessageId, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)));
        Assert.IsTrue(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) > 0);
    }

    [TestMethod]
    public void DecodePingTest()
    {
        byte[] bytes = HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePing(TestClientId));
        bool result = HealthCheckProtocol.TryDecode(bytes, out HealthCheckMessage message);

        Assert.IsTrue(result);
        Assert.AreEqual(TestClientId, message.ClientId);
        Assert.AreEqual(HealthCheckMessageType.Ping, message.Type);
        Assert.AreEqual("", message.Status);
    }

    [TestMethod]
    public void DecodePongTest()
    {
        byte[] bytes = HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePong(TestClientId));
        bool result = HealthCheckProtocol.TryDecode(bytes, out HealthCheckMessage message);

        Assert.IsTrue(result);
        Assert.AreEqual(TestClientId, message.ClientId);
        Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
        Assert.AreEqual("OK", message.Status);
    }

    [TestMethod]
    public void DecodeInvalidMessageTest()
    {
        bool result = HealthCheckProtocol.TryDecode(new byte[] { 1, 2, 3 }, out HealthCheckMessage message);

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

        Assert.IsTrue(HealthCheckProtocol.Send(client, HealthCheckProtocol.CreatePing(TestClientId)));
        Assert.IsTrue(HealthCheckProtocol.TryReceive(accepted, out HealthCheckMessage message));
        Assert.AreEqual(TestClientId, message.ClientId);
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

        Assert.IsTrue(HealthCheckProtocol.Send(accepted, HealthCheckProtocol.CreatePong(TestClientId)));
        Assert.IsTrue(HealthCheckProtocol.TryReceive(client, out HealthCheckMessage message));
        Assert.AreEqual(TestClientId, message.ClientId);
        Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
        Assert.AreEqual("OK", message.Status);
    }

    private static Socket CreateListener()
    {
        Socket listener = SocketFactory.CreateTcpSocket();
        listener.Bind(new IPEndPoint(IPAddress.Loopback, TestPort));
        listener.Listen(SocketFactory.ListenBacklog);
        return listener;
    }

    private static Socket CreateClient()
    {
        return SocketFactory.CreateTcpSocket();
    }
}
