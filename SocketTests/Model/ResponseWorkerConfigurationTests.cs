using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Reflection;
using SocketControl.Model;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class ResponseWorkerConfigurationTests
{
    [TestMethod]
    public void SocketServerClientResponsesUseMultipleWorkersTest()
    {
        Assert.AreEqual(4, ReadPrivateConstInt(typeof(TcpServer), "ClientResponseWorkerCount"));

        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketServer/Model/TcpServer.cs"));
        Assert.IsTrue(source.Contains("clientResponseChannel = Channel.CreateUnbounded<ClientResponseCommand>", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SingleReader = false", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ControlServerCommandResponsesUseMultipleWorkersTest()
    {
        Assert.AreEqual(4, ReadPrivateConstInt(typeof(ControlServer), "CommandResponseWorkerCount"));

        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketControl/Model/ControlServer.cs"));
        Assert.IsTrue(source.Contains("commandResponseChannel = Channel.CreateUnbounded<ControlResponseCommand>", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("SingleReader = false", StringComparison.Ordinal));
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

    private static int ReadPrivateConstInt(Type type, string fieldName)
    {
        FieldInfo field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsNotNull(field);
        return (int)field.GetRawConstantValue()!;
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
