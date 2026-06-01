using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketCommon.Model;

namespace SocketCommonTest.Model;

[TestClass]
public class HelloWorldProtocolTests
{
    private const int TestPort = 5001;

    [TestMethod]
    public void EncodeRequestTest()
    {
        byte[] bytes = HelloWorldProtocol.Encode(HelloWorldProtocol.CreateRequest());

        Assert.AreEqual("HELLOWORLD/1 REQUEST\n", Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    public void EncodeResponseTest()
    {
        byte[] bytes = HelloWorldProtocol.Encode(HelloWorldProtocol.CreateResponse());

        Assert.AreEqual("HELLOWORLD/1 RESPONSE Hello, World!\n", Encoding.UTF8.GetString(bytes));
    }

    [TestMethod]
    public void DecodeRequestTest()
    {
        bool result = HelloWorldProtocol.TryDecodeRequest("HELLOWORLD/1 REQUEST\n", out HelloWorldRequest request);

        Assert.IsTrue(result);
        Assert.IsNotNull(request);
    }

    [TestMethod]
    public void DecodeResponseTest()
    {
        bool result = HelloWorldProtocol.TryDecodeResponse("HELLOWORLD/1 RESPONSE Hello, World!\n", out HelloWorldResponse response);

        Assert.IsTrue(result);
        Assert.AreEqual("Hello, World!", response.Message);
    }

    [TestMethod]
    public void DecodeInvalidRequestTest()
    {
        bool result = HelloWorldProtocol.TryDecodeRequest("UNKNOWN\n", out HelloWorldRequest request);

        Assert.IsFalse(result);
        Assert.IsNull(request);
    }

    [TestMethod]
    public void DecodeInvalidResponseTest()
    {
        bool result = HelloWorldProtocol.TryDecodeResponse("HELLOWORLD/1 RESPONSE\n", out HelloWorldResponse response);

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

        Assert.IsTrue(HelloWorldProtocol.Send(client, HelloWorldProtocol.CreateRequest()));
        Assert.IsTrue(HelloWorldProtocol.TryReceiveRequest(accepted, out HelloWorldRequest request));
        Assert.IsNotNull(request);
    }

    [TestMethod]
    public async Task SendAndReceiveResponseTest()
    {
        using Socket listener = CreateListener();
        Task<Socket> acceptTask = listener.AcceptAsync();

        using Socket client = CreateClient();
        client.Connect(IPAddress.Loopback, TestPort);
        using Socket accepted = await acceptTask;

        Assert.IsTrue(HelloWorldProtocol.Send(accepted, HelloWorldProtocol.CreateResponse()));
        Assert.IsTrue(HelloWorldProtocol.TryReceiveResponse(client, out HelloWorldResponse response));
        Assert.AreEqual("Hello, World!", response.Message);
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
