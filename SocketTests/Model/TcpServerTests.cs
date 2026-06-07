using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketClient.Model;
using SocketCommon.Model;
using SocketServer.Model;
using AddressFamily = System.Net.Sockets.AddressFamily;
using ProtocolType = System.Net.Sockets.ProtocolType;
using Socket = System.Net.Sockets.Socket;
using SocketException = System.Net.Sockets.SocketException;
using SocketType = System.Net.Sockets.SocketType;

namespace SocketTests.Model;
[TestClass()]
public class TcpServerTests
{
    private const int TestPort = 5001;
    private Mutex? testPortMutex;
    private string? testCertificateDirectory;

    [TestInitialize]
    public void Initialize()
    {
        this.testCertificateDirectory = Path.Combine(
            Path.GetTempPath(),
            "socketserver-tcpserver-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testCertificateDirectory);
        SecureSocketConnection.Configure(CreateTestSecurityConfig(this.testCertificateDirectory));
        this.testPortMutex = new Mutex(false, "SocketServer.TestPort.5001");
        this.testPortMutex.WaitOne();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SocketFactory.Configure(new SocketOperationConfig());
        SecureSocketConnection.Configure(CreateTestSecurityConfig());
        DeleteTestCertificateDirectory(this.testCertificateDirectory);
        this.testPortMutex?.ReleaseMutex();
        this.testPortMutex?.Dispose();
    }

    private static SocketSecurityConfig CreateTestSecurityConfig(string? certificateDirectory = null)
    {
        return new SocketSecurityConfig
        {
            TransportMode = "Tls",
            TlsProtocol = "Auto",
            RequireTls13 = false,
            AuthenticationTimeoutMilliseconds = 30000,
            CertificateDirectory = certificateDirectory ?? ""
        };
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
            Assert.AreEqual(TcpServer.DefaultMaxConnections, status.MaxConnections);
            Assert.AreEqual(TcpServer.DefaultPendingAcceptCount, status.PendingAcceptCount);
            Assert.AreEqual(TcpServer.DefaultIdleTimeoutSeconds, status.IdleTimeoutSeconds);
            Assert.AreEqual(0, status.TotalRejectedClients);
            Assert.AreEqual(0, status.TotalIdleTimeoutClients);
            Assert.AreEqual(SocketFactory.ListenBacklog, status.ListenBacklog);
            Assert.AreEqual(SocketFactory.NoDelay, status.NoDelay);
            Assert.AreEqual(SocketMessageFrame.MaxPayloadLength, status.MaxPayloadLength);
            Assert.IsTrue(status.SocketAsyncEventArgsTotalCreatedCount >= SocketAsyncEventArgsFactory.InitialPoolSize);
            Assert.IsTrue(status.SocketAsyncEventArgsHighWatermarkInUseCount >= 0);
            Assert.IsTrue(status.SocketAsyncEventArgsGrowthCount >= 1);
            Assert.IsNotNull(status.StartedAt);
        }
        finally
        {
            server.End();
        }
    }

    [TestMethod()]
    public void BindInPortRangeSkipsUsedPortTest()
    {
        (int occupiedPort, int nextPort) = GetAvailableConsecutivePorts();
        using Socket occupied = new(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp);
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, occupiedPort));
        occupied.Listen(1);

        TcpServer server = new(1, "testServer", "127.0.0.1", 0);
        try
        {
            Assert.IsTrue(server.BindInPortRange(occupiedPort, nextPort));
            Assert.AreEqual(nextPort, server.GetPort());
        }
        finally
        {
            server.End();
        }
    }

    [TestMethod()]
    public void BindInPortRangeFailsWhenRangeIsExhaustedTest()
    {
        (int occupiedPort, _) = GetAvailableConsecutivePorts();
        using Socket occupied = new(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp);
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, occupiedPort));
        occupied.Listen(1);

        TcpServer server = new(1, "testServer", "127.0.0.1", 0);
        try
        {
            Assert.IsFalse(server.BindInPortRange(occupiedPort, occupiedPort));
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
    public async Task ClientRegisterRejectsCertificateClientIdMismatchTest()
    {
        SecureSocketConnection.Configure(CreateTestSecurityConfig());

        TcpServer server = new(1, "testServer", "127.0.0.1", 0);
        try
        {
            Assert.IsTrue(server.BindInPortRange(0, 0));
            Assert.IsTrue(server.Listen());
            Assert.IsTrue(server.StartClientAcceptLoop());

            using Socket clientSocket = SocketFactory.CreateTcpSocket();
            await SocketFactory.ConnectAsync(clientSocket, IPAddress.Loopback, server.GetPort());
            using SecureSocketConnection connection =
                await SecureSocketConnection.AuthenticateClientAsync(clientSocket, "SocketClient-123");

            Assert.IsTrue(await ClientMessageProtocol.SendRegisterAsync(connection, 456));
            (bool success, _) = await SocketMessageFrame.TryReceiveAsync(
                connection,
                headerTimeoutMilliseconds: 1000,
                payloadTimeoutMilliseconds: 1000);

            Assert.IsFalse(success);
        }
        finally
        {
            server.End();
            SecureSocketConnection.Configure(CreateTestSecurityConfig());
        }
    }

    [TestMethod()]
    public async Task ClientAcceptLoopHandlesMultipleClientsTest()
    {
        const int clientCount = 25;
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        List<TcpClient> clients = Enumerable.Range(1, clientCount)
            .Select(index => new TcpClient(index, $"testClient{index}", "127.0.0.1", TestPort))
            .ToList();

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

            await WithTimeoutAsync(Task.WhenAll(clientTasks));
            TcpServerStatus status = await WaitForStatusAsync(server, s => s.TotalReceivedMessages >= clientCount * 2);
            Assert.AreEqual(clientCount, status.TotalAcceptedClients);
            Assert.AreEqual(clientCount * 2, status.TotalReceivedMessages);
            Assert.AreEqual(clientCount * 2, status.TotalSentMessages);
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

    [TestMethod()]
    public async Task MaxConnectionLimitRejectsExtraClientsTest()
    {
        TcpServer server = new(
            1,
            "testServer",
            "127.0.0.1",
            TestPort,
            maxConnections: 1,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(30));
        TcpClient firstClient = new(1, "firstClient", "127.0.0.1", TestPort);
        TcpClient secondClient = new(2, "secondClient", "127.0.0.1", TestPort);

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(firstClient.Connect());
            await WaitForStatusAsync(server, s => s.ConnectedClientCount == 1);

            Assert.IsFalse(secondClient.Connect());
            TcpServerStatus status = await WaitForStatusAsync(server, s => s.TotalRejectedClients >= 1);

            Assert.AreEqual(1, status.ConnectedClientCount);
            Assert.AreEqual(1, status.MaxConnections);
            Assert.IsTrue(await firstClient.SendHealthCheckAsync());
            (bool healthCheckReceived, HealthCheckMessage message) = await WithTimeoutAsync(firstClient.TryReceiveHealthCheckAsync());
            Assert.IsTrue(healthCheckReceived);
            Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
        }
        finally
        {
            firstClient.Disconnect();
            secondClient.Disconnect();
            server.End();
        }
    }

    [TestMethod()]
    public async Task DuplicateClientIdRegisterRejectsNewConnectionAndKeepsExistingTest()
    {
        TcpServer server = new(
            1,
            "testServer",
            "127.0.0.1",
            TestPort,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(90));
        TcpClient firstClient = new(77, "firstClient", "127.0.0.1", TestPort);
        TcpClient secondClient = new(77, "secondClient", "127.0.0.1", TestPort);

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(firstClient.Connect());
            Assert.IsTrue(await firstClient.RegisterClientAsync());

            Assert.IsTrue(secondClient.Connect());
            (bool registerReceived, ClientRegisterAck duplicateAck) = await secondClient.RegisterClientWithAckAsync();

            Assert.IsTrue(registerReceived);
            Assert.IsNotNull(duplicateAck);
            Assert.IsFalse(duplicateAck.Success);
            Assert.AreEqual((uint)77, duplicateAck.ClientId);
            Assert.AreEqual(90, duplicateAck.RetryAfterSeconds);
            await WaitForStatusAsync(server, status => status.ConnectedClientCount == 1);
            Assert.IsTrue(await firstClient.SendHealthCheckAsync());
            (bool healthCheckReceived, HealthCheckMessage message) = await WithTimeoutAsync(firstClient.TryReceiveHealthCheckAsync());
            Assert.IsTrue(healthCheckReceived);
            Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
        }
        finally
        {
            firstClient.Disconnect();
            secondClient.Disconnect();
            server.End();
        }
    }

    [TestMethod()]
    public async Task DuplicateClientIdRegisterSucceedsAfterExistingConnectionTimesOutTest()
    {
        TcpServer server = new(
            1,
            "testServer",
            "127.0.0.1",
            TestPort,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromMilliseconds(100),
            idleScanInterval: TimeSpan.FromMilliseconds(50));
        TcpClient firstClient = new(78, "firstClient", "127.0.0.1", TestPort);
        TcpClient secondClient = new(78, "secondClient", "127.0.0.1", TestPort);

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(firstClient.Connect());
            Assert.IsTrue(await firstClient.RegisterClientAsync());
            await WaitForStatusAsync(server, status => status.ConnectedClientCount == 1);
            await WaitForStatusAsync(server, status => status.TotalIdleTimeoutClients >= 1);

            Assert.IsTrue(secondClient.Connect());
            (bool registerReceived, ClientRegisterAck ack) = await secondClient.RegisterClientWithAckAsync();

            Assert.IsTrue(registerReceived);
            Assert.IsTrue(ack.Success);
            await WaitForStatusAsync(server, status => status.ConnectedClientCount == 1);
        }
        finally
        {
            firstClient.Disconnect();
            secondClient.Disconnect();
            server.End();
        }
    }

    [TestMethod()]
    public async Task SocketClientSessionRetriesAfterDuplicateRegisterBackoffTest()
    {
        TcpServer server = new(
            1,
            "testServer",
            "127.0.0.1",
            TestPort,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(3),
            idleScanInterval: TimeSpan.FromMilliseconds(100));
        TcpClient firstClient = new(79, "firstClient", "127.0.0.1", TestPort);
        using SocketClientSession retryingSession = new();

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(firstClient.Connect());
            Assert.IsTrue(await firstClient.RegisterClientAsync());

            bool initialSuccess = await retryingSession.ConnectAndRegisterAsync(
                79,
                "retryingClient",
                "127.0.0.1",
                TestPort,
                useControlServer: false,
                healthCheckIntervalSeconds: 60,
                reconnectRetrySeconds: 1,
                duplicateRejectBackoffSeconds: 1);

            Assert.IsFalse(initialSuccess);
            await WaitForStatusAsync(server, status => status.TotalIdleTimeoutClients >= 1, timeoutMilliseconds: 10000);
            await WaitForConditionAsync(() => retryingSession.IsRegistered, timeoutMilliseconds: 10000);
        }
        finally
        {
            firstClient.Disconnect();
            server.End();
        }
    }

    [TestMethod()]
    public async Task SocketClientSessionDisconnectStopsReconnectLoopTest()
    {
        TcpServer server = new(
            1,
            "testServer",
            "127.0.0.1",
            TestPort,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(10));
        using SocketClientSession session = new();

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(await session.ConnectAndRegisterAsync(
                80,
                "disconnectClient",
                "127.0.0.1",
                TestPort,
                useControlServer: false,
                healthCheckIntervalSeconds: 1,
                reconnectRetrySeconds: 1,
                duplicateRejectBackoffSeconds: 1));
            TcpServerStatus connectedStatus = await WaitForStatusAsync(server, status => status.ConnectedClientCount == 1);

            session.Disconnect();
            await WaitForStatusAsync(server, status => status.ConnectedClientCount == 0);
            await Task.Delay(1500);

            Assert.AreEqual(connectedStatus.TotalAcceptedClients, server.GetStatus().TotalAcceptedClients);
        }
        finally
        {
            server.End();
        }
    }

    [TestMethod()]
    public async Task CleanupSchedulerClosesInactiveClientAfterHealthCheckTimeoutTest()
    {
        TcpServer server = new(
            1,
            "testServer",
            "127.0.0.1",
            TestPort,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromMilliseconds(100),
            idleScanInterval: TimeSpan.FromMilliseconds(50));
        TcpClient client = new(1, "idleClient", "127.0.0.1", TestPort);

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(client.Connect());

            TcpServerStatus status = await WaitForStatusAsync(server, s => s.TotalIdleTimeoutClients >= 1);
            Assert.AreEqual(0, status.ConnectedClientCount);
            Assert.IsTrue(status.TotalClosedClients >= 1);
        }
        finally
        {
            client.Disconnect();
            server.End();
        }
    }

    [TestMethod()]
    public async Task HealthCheckLoopKeepsConnectionAliveTest()
    {
        TcpServer server = new(
            1,
            "testServer",
            "127.0.0.1",
            TestPort,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(1),
            idleScanInterval: TimeSpan.FromMilliseconds(50));
        TcpClient client = new(1, "keepAliveClient", "127.0.0.1", TestPort);

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(client.Connect());
            Assert.IsTrue(client.StartHealthCheckLoop(TimeSpan.FromMilliseconds(50)));

            await Task.Delay(450);
            TcpServerStatus status = server.GetStatus();
            Assert.AreEqual(1, status.ConnectedClientCount);
            Assert.AreEqual(0, status.TotalIdleTimeoutClients);
            Assert.IsTrue(status.TotalReceivedMessages >= 2);
            Assert.IsTrue(status.TotalSentMessages >= 2);
            Assert.AreEqual(0, status.TotalReceivedMessageBytes);
            Assert.AreEqual(0, status.TotalSentMessageBytes);
        }
        finally
        {
            client.Disconnect();
            server.End();
        }
    }

    [TestMethod()]
    public async Task MessageByteCountersExcludeHealthCheckFramesTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        TcpClient client = new(1, "testClient", "127.0.0.1", TestPort);

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(client.Connect());

            await SendHealthCheckAndHelloWorldAsync(client);

            int requestBytes = HelloWorldProtocol.Encode(HelloWorldProtocol.CreateRequest(1)).Length;
            int responseBytes = HelloWorldProtocol.Encode(HelloWorldProtocol.CreateResponse(1)).Length;
            TcpServerStatus status = await WaitForStatusAsync(
                server,
                snapshot => snapshot.TotalReceivedMessageBytes == requestBytes &&
                    snapshot.TotalSentMessageBytes == responseBytes);

            Assert.AreEqual(requestBytes, status.TotalReceivedMessageBytes);
            Assert.AreEqual(responseBytes, status.TotalSentMessageBytes);
        }
        finally
        {
            client.Disconnect();
            server.End();
        }
    }

    [TestMethod()]
    public async Task HealthCheckLoopDoesNotCloseWhenIntervalExceedsReadTimeoutTest()
    {
        SocketFactory.Configure(new SocketOperationConfig
        {
            ConnectTimeoutSeconds = 3,
            ReadTimeoutSeconds = 1,
            WriteTimeoutSeconds = 3
        });
        TcpServer server = new(
            1,
            "testServer",
            "127.0.0.1",
            TestPort,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(5),
            idleScanInterval: TimeSpan.FromMilliseconds(50));
        TcpClient client = new(1, "keepAliveClient", "127.0.0.1", TestPort);

        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(client.Connect());
            Assert.IsTrue(client.StartHealthCheckLoop(TimeSpan.FromMilliseconds(1100)));

            await Task.Delay(2600);
            TcpServerStatus status = server.GetStatus();
            Assert.AreEqual(1, status.ConnectedClientCount);
            Assert.AreEqual(0, status.TotalIdleTimeoutClients);
            Assert.IsTrue(status.TotalReceivedMessages >= 2);
            Assert.IsTrue(status.TotalSentMessages >= 2);
        }
        finally
        {
            client.Disconnect();
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

    private static async Task<TcpServerStatus> WaitForStatusAsync(
        TcpServer server,
        Func<TcpServerStatus, bool> predicate,
        int timeoutMilliseconds = 5000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds);
        TcpServerStatus status;

        do
        {
            status = server.GetStatus();
            if (predicate(status))
            {
                return status;
            }

            await Task.Delay(25);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail("Timed out waiting for expected server status.");
        return status;
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task, int timeoutMilliseconds = 5000)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeoutMilliseconds));
        if (completed != task)
        {
            Assert.Fail("Timed out waiting for task completion.");
        }

        return await task;
    }

    private static async Task WithTimeoutAsync(Task task, int timeoutMilliseconds = 5000)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeoutMilliseconds));
        if (completed != task)
        {
            Assert.Fail("Timed out waiting for task completion.");
        }

        await task;
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutMilliseconds = 5000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMilliseconds);
        do
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail("Timed out waiting for expected condition.");
    }

    private static (int First, int Second) GetAvailableConsecutivePorts()
    {
        for (int port = 5200; port < 65000; port += 2)
        {
            using Socket first = CreateUnboundSocket();
            using Socket second = CreateUnboundSocket();
            try
            {
                first.Bind(new IPEndPoint(IPAddress.Loopback, port));
                second.Bind(new IPEndPoint(IPAddress.Loopback, port + 1));
                return (port, port + 1);
            }
            catch (SocketException)
            {
            }
        }

        Assert.Fail("Could not find available consecutive TCP ports.");
        return (0, 0);
    }

    private static Socket CreateUnboundSocket()
    {
        return new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    }

    private static void DeleteTestCertificateDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
