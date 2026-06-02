using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using SocketCommon.Model;

namespace SocketTests.Model;

[TestClass]
public class SecureSocketConnectionTests
{
    [TestMethod]
    public void LocalCertificateIsCreatedPerModuleTest()
    {
        string path = LocalCertificateStore.GetCertificatePath("SocketTests");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var certificate = LocalCertificateStore.GetOrCreate("SocketTests");

        Assert.IsTrue(File.Exists(path));
        Assert.IsTrue(certificate.Subject.Contains(SecureSocketConnection.TargetHost));
    }

    [TestMethod]
    public async Task SecureConnectionNegotiatesTlsProtocolTest()
    {
        using Socket listener = SocketFactory.CreateTcpSocket();
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(SocketFactory.ListenBacklog);

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        Socket clientSocket = SocketFactory.CreateTcpSocket();
        Task connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
        Socket serverSocket = await listener.AcceptAsync();
        await connectTask;

        Task<SecureSocketConnection> serverTask =
            SecureSocketConnection.AuthenticateServerAsync(serverSocket, "SocketTests");
        Task<SecureSocketConnection> clientTask =
            SecureSocketConnection.AuthenticateClientAsync(clientSocket, "SocketTests");
        await Task.WhenAll(serverTask, clientTask);

        using SecureSocketConnection server = await serverTask;
        using SecureSocketConnection client = await clientTask;

        Assert.IsTrue(server.NegotiatedProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);
        Assert.IsTrue(client.NegotiatedProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);
    }
}
