using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class ConnectionSessionTests
{
    [TestMethod]
    public async Task MarkReceivedUpdatesLastReceivedAtTest()
    {
        using SocketPair pair = await SocketPair.CreateAsync();
        ConnectionSession session = new(7, pair.ServerSocket);
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
        ConnectionSession session = new(1, pair.ServerSocket);

        Assert.IsTrue(session.Close());
        Assert.IsTrue(session.IsClosed);
        Assert.IsFalse(session.Close());
    }

    private sealed class SocketPair : IDisposable
    {
        private SocketPair(Socket serverSocket, Socket clientSocket)
        {
            ServerSocket = serverSocket;
            ClientSocket = clientSocket;
        }

        public Socket ServerSocket { get; }

        public Socket ClientSocket { get; }

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

            return new SocketPair(serverSocket, clientSocket);
        }

        public void Dispose()
        {
            ServerSocket.Dispose();
            ClientSocket.Dispose();
        }
    }
}
