using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading.Tasks;
using SocketClient.Model;
using SocketCommon.Model;
using SocketServer.Model;

namespace SocketServer.Model.Tests;
[TestClass()]
public class TcpServerTests
{
    private const int TestPort = 5001;

    [TestMethod()]
    public void StartTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Start());
        Assert.AreEqual(TestPort, server.GetPort());
        server.End();
    }

    [TestMethod()]
    public void BindTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Bind());
        Assert.AreEqual(TestPort, server.GetPort());
        server.End();
    }

    [TestMethod()]
    public void ListenTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Bind());
        Assert.IsTrue(server.Listen());
        server.End();
    }

    [TestMethod()]
    public void EndTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Start());
        Assert.IsTrue(server.End());
    }

    [TestMethod()]
    public async Task HelloWorldResponseTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Start());
        Task<bool> serverTask = server.AcceptHelloWorldRequestAndRespondAsync();

        TcpClient client = new(1, "testClient", "127.0.0.1", TestPort);
        Assert.IsTrue(client.Connect());
        Assert.IsTrue(await client.SendHelloWorldRequestAsync());
        (bool responseReceived, HelloWorldResponse response) = await client.TryReceiveHelloWorldResponseAsync();
        Assert.IsTrue(responseReceived);
        Assert.AreEqual((uint)1, response.ClientId);
        Assert.AreEqual("Hello, World!", response.Message);
        Assert.IsTrue(await serverTask);

        client.Disconnect();
        server.End();
    }
}
