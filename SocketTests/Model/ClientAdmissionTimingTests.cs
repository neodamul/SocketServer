using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketCommon.Model;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Client = SocketClient.Model.TcpClient;
using SocketLoadTest;

namespace SocketTests.Model;

[TestClass]
public class ClientAdmissionTimingTests
{
    [TestMethod]
    public async Task SlowAdmissionObserverCannotBlockConnectAndDrainsExactlyOnce()
    {
        using Socket listener = CreateListener();
        using Client client = CreateClient(listener);
        using ManualResetEventSlim observerEntered = new(false);
        using ManualResetEventSlim releaseObserver = new(false);
        int callbackCount = 0;
        Observe(client, (_, _, _) =>
        {
            Interlocked.Increment(ref callbackCount);
            observerEntered.Set();
            Assert.IsTrue(releaseObserver.Wait(TimeSpan.FromSeconds(5)));
        });

        Task<SecureSocketConnection> accept = AcceptSecureAsync(listener);
        Task<bool> connect = client.ConnectAsync();
        using SecureSocketConnection connection = await accept.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(await connect.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(observerEntered.IsSet, "Admission observers must not run on the networking completion path.");

        Task<int> drain = Task.Run(client.DrainAdmissionObservations);
        Assert.IsTrue(observerEntered.Wait(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(drain.IsCompleted, "A blocked observer should block only the explicit drain operation.");

        releaseObserver.Set();
        Assert.AreEqual(2, await drain.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(2, callbackCount);
        Assert.AreEqual(0, client.DrainAdmissionObservations(), "Each observation must be delivered exactly once.");
    }

    [TestMethod]
    public async Task ConnectReportsSeparateTcpAndTlsStagesAndIgnoresObserverExceptions()
    {
        using Socket listener = CreateListener();
        using Client client = CreateClient(listener);
        ConcurrentQueue<(string Stage, TimeSpan Elapsed, bool Success)> observations = new();
        Observe(client, (stage, elapsed, success) =>
        {
            observations.Enqueue((stage, elapsed, success));
            throw new InvalidOperationException("Observer failure");
        });

        Task<SecureSocketConnection> accept = AcceptSecureAsync(listener);
        Assert.IsTrue(await client.ConnectAsync());
        using SecureSocketConnection connection = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(SocketTransportSecurityMode.Tls, connection.TransportMode);
        Assert.AreEqual(2, client.DrainAdmissionObservations());
        CollectionAssert.AreEqual(new[] { "tcp_connect", "tls_authenticate" }, observations.Select(x => x.Stage).ToArray());
        Assert.IsTrue(observations.All(x => x.Success && x.Elapsed > TimeSpan.Zero));
    }

    [TestMethod]
    public async Task RefusedTcpConnectReportsOnlyFailedTcpStage()
    {
        using Socket reserved = SocketFactory.CreateTcpSocket();
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using Client client = CreateClient(reserved);
        ConcurrentQueue<(string Stage, TimeSpan Elapsed, bool Success)> observations = Observe(client);

        Assert.IsFalse(await client.ConnectAsync());
        Assert.AreEqual(1, client.DrainAdmissionObservations());
        Assert.AreEqual(1, observations.Count);
        Assert.AreEqual("tcp_connect", observations.Single().Stage);
        Assert.IsFalse(observations.Single().Success);
        Assert.IsTrue(observations.Single().Elapsed > TimeSpan.Zero);
    }

    [TestMethod]
    public async Task ClosedPeerReportsSuccessfulTcpAndFailedTls()
    {
        using Socket listener = CreateListener();
        using Client client = CreateClient(listener);
        var observations = Observe(client);
        Task peer = Task.Run(async () => { using Socket socket = await listener.AcceptAsync(); });

        Assert.IsFalse(await client.ConnectAsync());
        await peer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(2, client.DrainAdmissionObservations());
        CollectionAssert.AreEqual(new[] { "tcp_connect", "tls_authenticate" }, observations.Select(x => x.Stage).ToArray());
        CollectionAssert.AreEqual(new[] { true, false }, observations.Select(x => x.Success).ToArray());
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RegistrationReportsAcknowledgedOutcome(bool accepted)
    {
        using Socket listener = CreateListener();
        using Client client = CreateClient(listener);
        var observations = Observe(client);
        Task<SecureSocketConnection> accept = AcceptSecureAsync(listener);
        Assert.IsTrue(await client.ConnectAsync());
        using SecureSocketConnection connection = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        for (int index = 0; index < 2; index++)
        {
            Task<(bool Success, ClientRegisterAck Ack)> register = client.RegisterClientWithAckAsync();
            (bool received, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(connection);
            Assert.IsTrue(received);
            Assert.IsTrue(ClientMessageProtocol.TryDecodeRegister(frame, out ClientRegisterRequest request));
            Assert.IsTrue(await SocketMessageFrame.SendAsync(connection,
                ClientMessageProtocol.CreateFrame(request.ClientId, ClientMessageIds.ClientRegisterAck,
                    new ClientRegisterAck { ClientId = request.ClientId, Success = accepted })));
            var result = await register.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(result.Success);
            Assert.AreEqual(accepted, result.Ack.Success);
        }

        Assert.AreEqual(4, client.DrainAdmissionObservations());
        var registrations = observations.Where(x => x.Stage == "register").ToArray();
        Assert.AreEqual(2, registrations.Length, "Each real wire registration must be measured once.");
        Assert.IsTrue(registrations.All(x => x.Success == accepted && x.Elapsed > TimeSpan.Zero));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RouteRetriesReportSeparateFailures(bool pooled)
    {
        using Socket listener = CreateListener();
        using Client client = new(31, "admission-test");
        var observations = Observe(client);
        using PersistentSecureChannelPool pool = new("127.0.0.1", Port(listener), "SocketClient", 1);
        Task peer = Task.Run(async () =>
        {
            SecureSocketConnection? connection = null;
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (connection == null) connection = await AcceptSecureAsync(listener);
                    await RespondToRouteAsync(connection, false, 0);
                    if (!pooled) { connection.Dispose(); connection = null; }
                }
            }
            finally { connection?.Dispose(); }
        });

        bool result = pooled
            ? await client.ConnectViaControlChannelPoolAsync(pool, "test-control")
            : await client.ConnectViaControlServerAsync("127.0.0.1", Port(listener));
        Assert.IsFalse(result);
        await peer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(3, client.DrainAdmissionObservations());
        Assert.AreEqual(3, observations.Count);
        Assert.IsTrue(observations.All(x => x.Stage == "route_lookup" && !x.Success && x.Elapsed > TimeSpan.Zero));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SuccessfulRouteCompletesBeforeBackendTls(bool pooled)
    {
        using Socket control = CreateListener();
        using Socket backend = CreateListener();
        using Client client = new(32, "admission-test");
        var observations = Observe(client);
        using PersistentSecureChannelPool pool = new("127.0.0.1", Port(control), "SocketClient", 1);
        Task peer = Task.Run(async () =>
        {
            using SecureSocketConnection connection = await AcceptSecureAsync(control);
            await RespondToRouteAsync(connection, true, Port(backend));
        });
        Task<bool> connect = pooled
            ? client.ConnectViaControlChannelPoolAsync(pool, "test-control", 1)
            : client.ConnectViaControlServersAsync(new[] { new IPEndPoint(IPAddress.Loopback, Port(control)) }, 1);
        using Socket accepted = await backend.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.AreEqual(0, observations.Count, "Route observations must remain buffered until an explicit drain.");
            Assert.IsFalse(connect.IsCompleted, "Backend TLS has not started on the server yet.");
        }
        finally
        {
            using SecureSocketConnection connection = await SecureSocketConnection.AuthenticateServerAsync(accepted, "SocketTests");
            Assert.IsTrue(await connect.WaitAsync(TimeSpan.FromSeconds(10)));
            await peer.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.AreEqual(3, client.DrainAdmissionObservations());
        CollectionAssert.AreEqual(new[] { "route_lookup", "tcp_connect", "tls_authenticate" }, observations.Select(x => x.Stage).ToArray());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RouteIncludesControlTlsWait(bool pooled)
    {
        using Socket listener = CreateListener();
        using Client client = new(33, "admission-test");
        var observations = Observe(client);
        using PersistentSecureChannelPool pool = new("127.0.0.1", Port(listener), "SocketClient", 1);
        Task<bool> route = pooled
            ? client.ConnectViaControlChannelPoolAsync(pool, "test-control", 1)
            : client.ConnectViaControlServersAsync(new[] { new IPEndPoint(IPAddress.Loopback, Port(listener)) }, 1);
        using Socket accepted = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(10));
        long start = Stopwatch.GetTimestamp();
        await Task.Delay(100);
        TimeSpan held = Stopwatch.GetElapsedTime(start);
        Assert.AreEqual(0, observations.Count);
        using SecureSocketConnection connection = await SecureSocketConnection.AuthenticateServerAsync(accepted, "SocketTests");
        await RespondToRouteAsync(connection, false, 0);
        Assert.IsFalse(await route.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, client.DrainAdmissionObservations());
        Assert.AreEqual("route_lookup", observations.Single().Stage);
        Assert.IsTrue(observations.Single().Elapsed >= held);
    }

    [TestMethod]
    public async Task LaterBatchWarmupDoesNotBlockFirstBatchAndIsExcludedFromRamp()
    {
        ManualAdmissionClock clock = new();
        ActiveAdmissionStopwatch admissionStopwatch = new(clock);
        admissionStopwatch.Start();
        List<string> events = new();
        List<int[]> warmedBatches = new();
        double? allClientsReadyMilliseconds = null;

        async Task Warmup(IReadOnlyCollection<int> clientIds, CancellationToken _)
        {
            warmedBatches.Add(clientIds.ToArray());
            events.Add($"warmup-{clientIds.Single()}");
            clock.Advance(TimeSpan.FromSeconds(5));
            await Task.CompletedTask;
        }

        async Task<int[]> Connect(IReadOnlyCollection<int> clientIds)
        {
            events.Add($"connect-{clientIds.Single()}");
            clock.Advance(TimeSpan.FromMilliseconds(25));
            await Task.CompletedTask;
            return clientIds.ToArray();
        }

        await AdmissionBatchRunner.RunAsync(
            admissionStopwatch,
            new[] { 101 },
            Warmup,
            () => Connect(new[] { 101 }),
            CancellationToken.None);
        allClientsReadyMilliseconds = admissionStopwatch.Elapsed.TotalMilliseconds;
        await AdmissionBatchRunner.RunAsync(
            admissionStopwatch,
            new[] { 102 },
            Warmup,
            () => Connect(new[] { 102 }),
            CancellationToken.None);
        admissionStopwatch.Pause();

        CollectionAssert.AreEqual(new[] { "warmup-101", "connect-101", "warmup-102", "connect-102" }, events);
        Assert.AreEqual(2, warmedBatches.Count);
        CollectionAssert.AreEqual(new[] { 101 }, warmedBatches[0]);
        CollectionAssert.AreEqual(new[] { 102 }, warmedBatches[1]);
        Assert.AreEqual(25, allClientsReadyMilliseconds,
            "The first ready batch timestamp must exclude the later batch warm-up interval.");
        Assert.AreEqual(50, admissionStopwatch.Elapsed.TotalMilliseconds,
            "Both certificate warm-up intervals must be excluded from admission ramp timing.");
    }

    [TestMethod]
    public async Task AdmissionBatchRunnerResumesAdmissionWhenWarmupFails()
    {
        ManualAdmissionClock clock = new();
        ActiveAdmissionStopwatch admissionStopwatch = new(clock);
        admissionStopwatch.Start();
        clock.Advance(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => AdmissionBatchRunner.RunAsync(
            admissionStopwatch,
            new[] { 101 },
            async (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                await Task.CompletedTask;
                throw new InvalidOperationException("warm-up failed");
            },
            () => Task.FromResult(0),
            CancellationToken.None));

        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert.AreEqual(30, admissionStopwatch.Elapsed.TotalMilliseconds,
            "The stopwatch must resume after a failed warm-up without counting the failed warm-up interval.");
    }

    [TestMethod]
    public async Task PooledRouteIncludesQueueWait()
    {
        using Socket listener = CreateListener();
        using Client client = new(34, "admission-test");
        var observations = Observe(client);
        using PersistentSecureChannelPool pool = new("127.0.0.1", Port(listener), "SocketClient", 1);
        Task<SecureSocketConnection> accept = AcceptSecureAsync(listener);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(bool Success, SocketMessageFrame Frame)> occupied = pool.SendAndReceiveAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
            return (true, null!);
        });
        using SecureSocketConnection connection = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<bool> route = client.ConnectViaControlChannelPoolAsync(pool, "test-control", 1);
        long start = Stopwatch.GetTimestamp();
        TimeSpan held;
        try
        {
            await Task.Delay(100);
            held = Stopwatch.GetElapsedTime(start);
            Assert.AreEqual(0, observations.Count);
            Assert.IsFalse(route.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await occupied.WaitAsync(TimeSpan.FromSeconds(10));
        await RespondToRouteAsync(connection, false, 0);
        Assert.IsFalse(await route.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, client.DrainAdmissionObservations());
        Assert.AreEqual("route_lookup", observations.Single().Stage);
        Assert.IsTrue(observations.Single().Elapsed >= held);
    }

    [TestMethod]
    public async Task DisposedPoolReportsFailureEvenWhenCallbackThrows()
    {
        using Client client = new(35, "admission-test");
        int count = 0;
        Observe(client, (stage, elapsed, success) =>
        {
            if (stage == "route_lookup" && elapsed > TimeSpan.Zero && !success) count++;
            throw new InvalidOperationException("Observer failure");
        });
        using PersistentSecureChannelPool pool = new("127.0.0.1", 25001, "SocketClient", 1);
        pool.Dispose();
        Assert.IsFalse(await client.ConnectViaControlChannelPoolAsync(pool, "test-control", 2));
        Assert.AreEqual(2, client.DrainAdmissionObservations());
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public async Task RegistrationWithoutSocketDoesNotReportUnattemptedStage()
    {
        using Client client = new();
        var observations = Observe(client);
        Assert.IsFalse((await client.RegisterClientWithAckAsync()).Success);
        Assert.AreEqual(0, observations.Count);
    }

    private static ConcurrentQueue<(string Stage, TimeSpan Elapsed, bool Success)> Observe(Client client)
    {
        ConcurrentQueue<(string Stage, TimeSpan Elapsed, bool Success)> observations = new();
        Observe(client, (stage, elapsed, success) => observations.Enqueue((stage, elapsed, success)));
        return observations;
    }

    private static void Observe(Client client, Action<string, TimeSpan, bool> callback)
    {
        var property = typeof(Client).GetProperty("AdmissionStageCompleted");
        Assert.IsNotNull(property, "TcpClient must expose AdmissionStageCompleted.");
        Assert.AreEqual(typeof(Action<string, TimeSpan, bool>), property!.PropertyType);
        property.SetValue(client, callback);
    }

    private static Socket CreateListener()
    {
        Socket listener = SocketFactory.CreateTcpSocket();
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Assert.IsTrue(Port(listener) >= 2000);
        listener.Listen(SocketFactory.ListenBacklog);
        return listener;
    }

    private sealed class ManualAdmissionClock : IAdmissionClock
    {
        private long timestamp;

        public long GetTimestamp() => this.timestamp;

        public TimeSpan GetElapsed(long startTimestamp, long endTimestamp) =>
            TimeSpan.FromMilliseconds(endTimestamp - startTimestamp);

        public void Advance(TimeSpan duration)
        {
            this.timestamp += (long)duration.TotalMilliseconds;
        }
    }

    private static int Port(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private static Client CreateClient(Socket socket)
    {
        Assert.IsTrue(Port(socket) >= 2000);
        return new Client(30, "admission-test", "127.0.0.1", Port(socket));
    }

    private static async Task<SecureSocketConnection> AcceptSecureAsync(Socket listener)
    {
        Socket accepted = await listener.AcceptAsync();
        return await SecureSocketConnection.AuthenticateServerAsync(accepted, "SocketTests");
    }

    private static async Task RespondToRouteAsync(SecureSocketConnection connection, bool success, int port)
    {
        (bool received, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(connection);
        Assert.IsTrue(received);
        Assert.IsTrue(ControlProtocol.TryDecode(frame, ControlMessageIds.RouteRequest, out RouteRequest request));
        Assert.IsTrue(await ControlProtocol.SendAsync(connection, request.ClientId, ControlMessageIds.RouteResponse,
            new RouteResponse { Success = success, Host = "127.0.0.1", Port = port, InstanceId = "test-server" }));
    }
}
