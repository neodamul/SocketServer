using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SocketCommon.Model;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class ConnectionSessionTests
{
    [TestMethod]
    public async Task MarkReceivedUpdatesLastReceivedAtTest()
    {
        using SocketPair pair = await SocketPair.CreateAsync();
        ConnectionSession session = new(7, pair.ServerConnection);
        DateTimeOffset initialLastReceivedAt = session.LastReceivedAt;

        await Task.Delay(10);
        session.MarkReceived();

        Assert.AreEqual(7, session.Id);
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.RemoteEndPoint));
        Assert.IsTrue(session.ConnectedAt <= initialLastReceivedAt);
        Assert.IsTrue(session.LastReceivedAt > initialLastReceivedAt);
    }

    [TestMethod]
    public async Task CloseIsIdempotentTest()
    {
        using SocketPair pair = await SocketPair.CreateAsync();
        ConnectionSession session = new(1, pair.ServerConnection);

        Assert.IsTrue(session.Close());
        Assert.IsTrue(session.IsClosed);
        Assert.IsFalse(session.Close());
    }

    private sealed class SocketPair : IDisposable
    {
        private SocketPair(SecureSocketConnection serverConnection, SecureSocketConnection clientConnection)
        {
            ServerConnection = serverConnection;
            ClientConnection = clientConnection;
        }

        public SecureSocketConnection ServerConnection { get; }

        public SecureSocketConnection ClientConnection { get; }

        public static async Task<SocketPair> CreateAsync()
        {
            using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);

            Assert.IsNotNull(listener.LocalEndPoint);
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Task connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
            Socket serverSocket = await listener.AcceptAsync();
            await connectTask;
            Task<SecureSocketConnection> serverConnectionTask =
                SecureSocketConnection.AuthenticateServerAsync(serverSocket, "SocketTests");
            Task<SecureSocketConnection> clientConnectionTask =
                SecureSocketConnection.AuthenticateClientAsync(clientSocket, "SocketTests");
            await Task.WhenAll(serverConnectionTask, clientConnectionTask);

            return new SocketPair(await serverConnectionTask, await clientConnectionTask);
        }

        public void Dispose()
        {
            ServerConnection.Dispose();
            ClientConnection.Dispose();
        }
    }
}
