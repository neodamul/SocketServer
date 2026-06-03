using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Model;

namespace SocketTests.Model;

[TestClass]
public class SecureSocketConnectionTests
{
    private const string TestPasswordVariable = "SOCKET_TEST_CERTIFICATE_PASSWORD";

    [TestMethod]
    public void LocalCertificateIsCreatedPerModuleTest()
    {
        string path = LocalCertificateStore.GetCertificatePath("SocketTests");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var certificate = LocalCertificateStore.GetOrCreate("SocketTests");
        using var rootCertificate = LocalCertificateStore.GetOrCreateRootAuthority();

        Assert.IsTrue(File.Exists(path));
        Assert.IsTrue(File.Exists(LocalCertificateStore.GetRootCertificatePath()));
        Assert.IsTrue(certificate.Subject.Contains(SecureSocketConnection.TargetHost));
        Assert.AreEqual(rootCertificate.Subject, certificate.Issuer);
        Assert.IsTrue(LocalCertificateStore.IsSignedByRoot(certificate, rootCertificate));
    }

    [TestMethod]
    public async Task SecureConnectionNegotiatesTlsProtocolTest()
    {
        SecureSocketConnection.Configure(CreateSecurityConfig());
        using Socket listener = SocketFactory.CreateTcpSocket();
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(SocketFactory.ListenBacklog);

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        Socket clientSocket = SocketFactory.CreateTcpSocket();
        Task connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
        Socket serverSocket = await listener.AcceptAsync();
        await connectTask;

        Task<SecureSocketConnection> serverTask =
            SecureSocketConnection.AuthenticateServerAsync(serverSocket, "SocketTestsServer");
        Task<SecureSocketConnection> clientTask =
            SecureSocketConnection.AuthenticateClientAsync(clientSocket, "SocketTestsClient");
        await Task.WhenAll(serverTask, clientTask);

        using SecureSocketConnection server = await serverTask;
        using SecureSocketConnection client = await clientTask;

        Assert.IsTrue(server.NegotiatedProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);
        Assert.IsTrue(client.NegotiatedProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);
    }

    [TestMethod]
    public void SecureSocketOptionsCanBeConfiguredFromConfigTest()
    {
        SecureSocketConnection.Configure(new SocketSecurityConfig
        {
            TlsProtocol = "Tls13",
            RequireTls13 = true,
            RequireClientCertificate = true,
            CertificatePasswordEnvironmentVariable = TestPasswordVariable,
            CertificateRenewBeforeDays = 15,
            RootCertificateLifetimeYears = 8,
            ModuleCertificateLifetimeYears = 3,
            AuthenticationTimeoutMilliseconds = 2500
        });

        Assert.AreEqual(SslProtocols.Tls13, SecureSocketConnection.ConfiguredProtocols);
        Assert.IsTrue(SecureSocketConnection.RequireTls13);
        Assert.IsTrue(SecureSocketConnection.RequireClientCertificate);
        Assert.AreEqual(2500, SecureSocketConnection.AuthenticationTimeoutMilliseconds);

        SecureSocketConnection.Configure(CreateSecurityConfig());
    }

    [TestMethod]
    public void CertificatePasswordCanBeProvidedByEnvironmentVariableTest()
    {
        string directory = CreateTemporaryCertificateDirectory();
        Environment.SetEnvironmentVariable(TestPasswordVariable, $"password-{Guid.NewGuid():N}");
        try
        {
            SecureSocketConnection.Configure(CreateSecurityConfig(directory));

            using var certificate = LocalCertificateStore.GetOrCreate("SocketTestsPassword");

            Assert.IsTrue(File.Exists(LocalCertificateStore.GetRootCertificatePath()));
            Assert.IsTrue(File.Exists(LocalCertificateStore.GetCertificatePath("SocketTestsPassword")));
            Assert.IsTrue(certificate.HasPrivateKey);
            Assert.IsTrue(certificate.NotAfter > DateTime.UtcNow.AddYears(1));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestPasswordVariable, null);
            DeleteTemporaryCertificateDirectory(directory);
            SecureSocketConnection.Configure(CreateSecurityConfig());
        }
    }

    [TestMethod]
    public async Task SecureConnectionSupportsMutualTlsWhenConfiguredTest()
    {
        string directory = CreateTemporaryCertificateDirectory();
        Environment.SetEnvironmentVariable(TestPasswordVariable, $"password-{Guid.NewGuid():N}");
        try
        {
            SecureSocketConnection.Configure(CreateSecurityConfig(directory, requireClientCertificate: true));

            using Socket listener = SocketFactory.CreateTcpSocket();
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(SocketFactory.ListenBacklog);

            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            Socket clientSocket = SocketFactory.CreateTcpSocket();
            Task connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
            Socket serverSocket = await listener.AcceptAsync();
            await connectTask;

            Task<SecureSocketConnection> serverTask =
                SecureSocketConnection.AuthenticateServerAsync(serverSocket, "SocketTestsMtlsServer");
            Task<SecureSocketConnection> clientTask =
                SecureSocketConnection.AuthenticateClientAsync(clientSocket, "SocketTestsMtlsClient");
            await Task.WhenAll(serverTask, clientTask);

            using SecureSocketConnection server = await serverTask;
            using SecureSocketConnection client = await clientTask;

            Assert.IsTrue(server.NegotiatedProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);
            Assert.IsTrue(client.NegotiatedProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestPasswordVariable, null);
            DeleteTemporaryCertificateDirectory(directory);
            SecureSocketConnection.Configure(CreateSecurityConfig());
        }
    }

    [TestMethod]
    public void CertificateIsRenewedWhenItIsInsideRotationWindowTest()
    {
        string directory = CreateTemporaryCertificateDirectory();
        Environment.SetEnvironmentVariable(TestPasswordVariable, $"password-{Guid.NewGuid():N}");
        try
        {
            SecureSocketConnection.Configure(CreateSecurityConfig(directory));
            using X509Certificate2 firstCertificate = LocalCertificateStore.GetOrCreate("SocketTestsRotation");
            string firstSerialNumber = Convert.ToHexString(firstCertificate.GetSerialNumber());

            SecureSocketConnection.Configure(CreateSecurityConfig(directory, certificateRenewBeforeDays: 800));
            using X509Certificate2 renewedCertificate = LocalCertificateStore.GetOrCreate("SocketTestsRotation");
            string renewedSerialNumber = Convert.ToHexString(renewedCertificate.GetSerialNumber());

            Assert.AreNotEqual(firstSerialNumber, renewedSerialNumber);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestPasswordVariable, null);
            DeleteTemporaryCertificateDirectory(directory);
            SecureSocketConnection.Configure(CreateSecurityConfig());
        }
    }

    private static SocketSecurityConfig CreateSecurityConfig(
        string certificateDirectory = "",
        bool requireClientCertificate = false,
        int certificateRenewBeforeDays = 30)
    {
        return new SocketSecurityConfig
        {
            TlsProtocol = "Auto",
            RequireTls13 = false,
            RequireClientCertificate = requireClientCertificate,
            CertificateDirectory = certificateDirectory,
            CertificatePasswordEnvironmentVariable = TestPasswordVariable,
            CertificateRenewBeforeDays = certificateRenewBeforeDays,
            RootCertificateLifetimeYears = 10,
            ModuleCertificateLifetimeYears = 2,
            AuthenticationTimeoutMilliseconds = 5000
        };
    }

    private static string CreateTemporaryCertificateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"socket-certificates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryCertificateDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
