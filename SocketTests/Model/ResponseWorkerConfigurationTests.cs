using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Reflection;
using SocketCommon.Model;
using SocketControl.Model;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class ResponseWorkerConfigurationTests
{
    [TestMethod]
    public void SocketServerClientResponsesUseMultipleWorkersTest()
    {
        Assert.AreEqual(4, InvokePrivateStaticInt(typeof(TcpServer), "CalculateWorkerCount", 100, 500, 4, 64));
        Assert.AreEqual(20, InvokePrivateStaticInt(typeof(TcpServer), "CalculateWorkerCount", 10000, 500, 4, 64));

        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketServer/Model/TcpServer.cs"));
        Assert.IsTrue(source.Contains("clientResponseChannel = Channel.CreateUnbounded<ClientResponseCommand>", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("GetClientResponseWorkerCount()", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SingleReader = false", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ControlServerCommandResponsesUseMultipleWorkersTest()
    {
        Assert.AreEqual(4, InvokePrivateStaticInt(typeof(ControlServer), "CalculateWorkerCount", 1, 2, 4, 64));
        Assert.AreEqual(16, InvokePrivateStaticInt(typeof(ControlServer), "CalculateWorkerCount", 8, 2, 4, 64));
        Assert.AreEqual(64, InvokePrivateStaticInt(typeof(ControlServer), "CalculateWorkerCount", 128, 2, 4, 64));

        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketControl/Model/ControlServer.cs"));
        Assert.IsTrue(source.Contains("GetCommandWorkerCount()", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("GetCommandResponseWorkerCount()", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("commandRequestChannel = Channel.CreateUnbounded<ControlCommandRequest>", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("commandResponseChannel = Channel.CreateUnbounded<ControlResponseCommand>", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SingleReader = false", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ControlServerClosesClientRouteRequestAfterResponseTest()
    {
        Assert.IsTrue(InvokePrivateStaticBool(typeof(ControlServer), "ShouldCloseAfterResponse", ControlMessageIds.RouteRequest));
        Assert.IsFalse(InvokePrivateStaticBool(typeof(ControlServer), "ShouldCloseAfterResponse", ControlMessageIds.ServerHeartbeat));
        Assert.IsFalse(InvokePrivateStaticBool(typeof(ControlServer), "ShouldCloseAfterResponse", ControlMessageIds.RegistrySnapshotRequest));

        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketControl/Model/ControlServer.cs"));
        Assert.IsTrue(source.Contains("ShouldCloseAfterResponse(frame.MessageId)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SocketServerSessionReportsUseMultiplePersistentChannelsPerEndpointTest()
    {
        Assert.AreEqual(4, InvokePrivateStaticInt(typeof(ControlServerReporter), "CalculateSessionReportWorkerCount", 100));
        Assert.AreEqual(20, InvokePrivateStaticInt(typeof(ControlServerReporter), "CalculateSessionReportWorkerCount", 10000));

        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketServer/Model/ControlServerReporter.cs"));
        Assert.IsTrue(source.Contains("reportChannels = CreateReportChannels(sessionReportWorkerCount)", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SingleReader = true", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Task[] workers = new Task[channels.Length]", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("CreateConnectionGroups(controlServers, this.reportTimeout, sessionReportWorkerCount)", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("Math.Max(2, channelCount)", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("GetSessionEventPartitionKey(clientId, payload)", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("GetReportChannelWriter(message)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SocketServerHeartbeatUsesObservedStatusTimestampTest()
    {
        string serverSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketServer/Model/TcpServer.cs"));
        string reporterSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketServer/Model/ControlServerReporter.cs"));

        Assert.IsTrue(serverSource.Contains("DateTimeOffset observedAt = DateTimeOffset.UtcNow", StringComparison.Ordinal));
        Assert.IsTrue(serverSource.Contains("ObservedAt = observedAt", StringComparison.Ordinal));
        Assert.IsTrue(reporterSource.Contains("SentAt = status.ObservedAt == default ? DateTimeOffset.UtcNow : status.ObservedAt", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RelayWorkersBatchRepeatedMessagesTest()
    {
        string controlSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketControl/Model/ControlServer.cs"));
        string serverSource = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketServer/Model/TcpServer.cs"));

        Assert.IsTrue(controlSource.Contains("PeerRelayBatchFlushIntervalMilliseconds", StringComparison.Ordinal));
        Assert.IsTrue(controlSource.Contains("ControlMessageIds.ControlRelayBatch", StringComparison.Ordinal));
        Assert.IsTrue(controlSource.Contains("CollectPeerRelayBatchAsync", StringComparison.Ordinal));
        Assert.IsTrue(controlSource.Contains("PublishCommandsIndividuallyToPeerAsync", StringComparison.Ordinal));
        Assert.IsTrue(serverSource.Contains("ServerRelayBatchFlushIntervalMilliseconds", StringComparison.Ordinal));
        Assert.IsTrue(serverSource.Contains("ServerRelayMessageIds.ServerRelayBatch", StringComparison.Ordinal));
        Assert.IsTrue(serverSource.Contains("CollectServerRelaySendBatchAsync", StringComparison.Ordinal));
        Assert.IsTrue(serverSource.Contains("SendRelayCommandsIndividuallyAsync", StringComparison.Ordinal));
        Assert.IsTrue(serverSource.Contains("resultsByIndex", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SocketServerSessionSendPreservesPerConnectionOrderTest()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketServer/Model/ConnectionSession.cs"));

        Assert.IsTrue(source.Contains("sendQueueTail", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SendAfterAsync(this.sendQueueTail", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ControlServerResponseSendPreservesPerConnectionOrderTest()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketControl/Model/ControlServer.cs"));

        Assert.IsTrue(source.Contains("activeConnectionsBySecureConnection", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SendAfterAsync(this.sendQueueTail", StringComparison.Ordinal));
    }

    private static int InvokePrivateStaticInt(Type type, string methodName, params object[] args)
    {
        MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsNotNull(method);
        return (int)method.Invoke(null, args)!;
    }

    private static bool InvokePrivateStaticBool(Type type, string methodName, params object[] args)
    {
        MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsNotNull(method);
        return (bool)method.Invoke(null, args)!;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SocketServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
