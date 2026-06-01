using System.Buffers.Binary;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketCommon.Model;

namespace SocketCommonTest.Model;

[TestClass]
public class HelloWorldProtocolTests
{
    private const int TestPort = 5001;
    private const uint TestClientId = 7;

    [TestMethod]
    public void EncodeRequestTest()
    {
        byte[] bytes = HelloWorldProtocol.Encode(HelloWorldProtocol.CreateRequest(TestClientId));

        Assert.AreEqual(SocketMessageFrame.HeaderLength, bytes.Length);
        Assert.AreEqual(TestClientId, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)));
        Assert.AreEqual(HelloWorldProtocol.RequestMessageId, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)));
        Assert.AreEqual((uint)0, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)));
    }

    [TestMethod]
    public void EncodeResponseTest()
    {
        byte[] bytes = HelloWorldProtocol.Encode(HelloWorldProtocol.CreateResponse(TestClientId));

        Assert.AreEqual(SocketMessageFrame.HeaderLength + HelloWorldProtocol.DefaultMessage.Length, bytes.Length);
        Assert.AreEqual(TestClientId, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)));
        Assert.AreEqual(HelloWorldProtocol.ResponseMessageId, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4)));
        Assert.AreEqual((uint)HelloWorldProtocol.DefaultMessage.Length, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)));
    }

    [TestMethod]
    public void DecodeRequestTest()
    {
        byte[] bytes = HelloWorldProtocol.Encode(HelloWorldProtocol.CreateRequest(TestClientId));
        bool result = HelloWorldProtocol.TryDecodeRequest(bytes, out HelloWorldRequest request);

        Assert.IsTrue(result);
        Assert.IsNotNull(request);
        Assert.AreEqual(TestClientId, request.ClientId);
    }

    [TestMethod]
    public void DecodeResponseTest()
    {
        byte[] bytes = HelloWorldProtocol.Encode(HelloWorldProtocol.CreateResponse(TestClientId));
        bool result = HelloWorldProtocol.TryDecodeResponse(bytes, out HelloWorldResponse response);

        Assert.IsTrue(result);
        Assert.AreEqual(TestClientId, response.ClientId);
        Assert.AreEqual("Hello, World!", response.Message);
    }

    [TestMethod]
    public void DecodeInvalidRequestTest()
    {
        bool result = HelloWorldProtocol.TryDecodeRequest(new byte[] { 1, 2, 3 }, out HelloWorldRequest request);

        Assert.IsFalse(result);
        Assert.IsNull(request);
    }

    [TestMethod]
    public void DecodeInvalidResponseTest()
    {
        bool result = HelloWorldProtocol.TryDecodeResponse(HelloWorldProtocol.Encode(HelloWorldProtocol.CreateRequest(TestClientId)), out HelloWorldResponse response);

        Assert.IsFalse(result);
        Assert.IsNull(response);
    }

    [TestMethod]
    public async Task SendAndReceiveRequestTest()
    {
        using Socket listener = CreateListener();
        Task<Socket> acceptTask = listener.AcceptAsync();

        using Socket client = CreateClient();
        client.Connect(IPAddress.Loopback, TestPort);
        using Socket accepted = await acceptTask;

        Assert.IsTrue(HelloWorldProtocol.Send(client, HelloWorldProtocol.CreateRequest(TestClientId)));
        Assert.IsTrue(HelloWorldProtocol.TryReceiveRequest(accepted, out HelloWorldRequest request));
        Assert.IsNotNull(request);
        Assert.AreEqual(TestClientId, request.ClientId);
    }

    [TestMethod]
    public async Task SendAndReceiveResponseTest()
    {
        using Socket listener = CreateListener();
        Task<Socket> acceptTask = listener.AcceptAsync();

        using Socket client = CreateClient();
        client.Connect(IPAddress.Loopback, TestPort);
        using Socket accepted = await acceptTask;

        Assert.IsTrue(HelloWorldProtocol.Send(accepted, HelloWorldProtocol.CreateResponse(TestClientId)));
        Assert.IsTrue(HelloWorldProtocol.TryReceiveResponse(client, out HelloWorldResponse response));
        Assert.AreEqual(TestClientId, response.ClientId);
        Assert.AreEqual("Hello, World!", response.Message);
    }

    [TestMethod]
    public void SendLargeResponseOverPayloadLimitTest()
    {
        string largeMessage = new('A', SocketMessageFrame.MaxPayloadLength + 1);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => HelloWorldProtocol.Encode(new HelloWorldResponse(TestClientId, largeMessage)));
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
