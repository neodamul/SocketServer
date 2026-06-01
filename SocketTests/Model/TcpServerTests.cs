using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SocketClient.Model;
using SocketCommon.Model;
using SocketServer.Model;

namespace SocketTests.Model;
[TestClass()]
public class TcpServerTests
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
    public void GetStatusTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());

            TcpServerStatus status = server.GetStatus();

            Assert.IsTrue(status.IsSocketInitialized);
            Assert.IsTrue(status.IsBound);
            Assert.IsTrue(status.IsListening);
            Assert.IsTrue(status.IsAcceptLoopRunning);
            Assert.AreEqual("127.0.0.1", status.IpAddress);
            Assert.AreEqual(TestPort, status.Port);
            Assert.AreEqual(SocketFactory.ListenBacklog, status.ListenBacklog);
            Assert.AreEqual(SocketFactory.NoDelay, status.NoDelay);
            Assert.AreEqual(SocketMessageFrame.MaxPayloadLength, status.MaxPayloadLength);
            Assert.IsNotNull(status.StartedAt);
        }
        finally
        {
            server.End();
        }
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

    [TestMethod()]
    public async Task ClientAcceptLoopHandlesMultipleClientsTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        List<TcpClient> clients = new()
        {
            new TcpClient(1, "testClient1", "127.0.0.1", TestPort),
            new TcpClient(2, "testClient2", "127.0.0.1", TestPort),
            new TcpClient(3, "testClient3", "127.0.0.1", TestPort),
        };

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());

            List<Task> clientTasks = new();
            foreach (TcpClient client in clients)
            {
                Assert.IsTrue(client.Connect());
                clientTasks.Add(SendHealthCheckAndHelloWorldAsync(client));
            }

            await Task.WhenAll(clientTasks);
        }
        finally
        {
            foreach (TcpClient client in clients)
            {
                client.Disconnect();
            }

            server.End();
        }
    }

    private static async Task SendHealthCheckAndHelloWorldAsync(TcpClient client)
    {
        Assert.IsTrue(await client.SendHealthCheckAsync());
        (bool healthCheckReceived, HealthCheckMessage healthCheckMessage) = await client.TryReceiveHealthCheckAsync();
        Assert.IsTrue(healthCheckReceived);
        Assert.AreEqual(HealthCheckMessageType.Pong, healthCheckMessage.Type);
        Assert.AreEqual("OK", healthCheckMessage.Status);

        Assert.IsTrue(await client.SendHelloWorldRequestAsync());
        (bool responseReceived, HelloWorldResponse response) = await client.TryReceiveHelloWorldResponseAsync();
        Assert.IsTrue(responseReceived);
        Assert.AreEqual("Hello, World!", response.Message);
    }
}
