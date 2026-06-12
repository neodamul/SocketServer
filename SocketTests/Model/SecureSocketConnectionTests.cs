using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
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
    private const string TestMessageSecretVariable = "SOCKET_TEST_MESSAGE_SECRET";

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
    public void LocalCertificateContainsServerAndClientEnhancedKeyUsagesTest()
    {
        using var certificate = LocalCertificateStore.GetOrCreate("SocketClient-123");

        Assert.IsTrue(LocalCertificateStore.HasEnhancedKeyUsage(
            certificate,
            SecureSocketConnection.ServerAuthenticationEkuOid));
        Assert.IsTrue(LocalCertificateStore.HasEnhancedKeyUsage(
            certificate,
            SecureSocketConnection.ClientAuthenticationEkuOid));
        Assert.IsFalse(LocalCertificateStore.HasEnhancedKeyUsage(certificate, "1.2.3.4"));
        Assert.IsTrue(LocalCertificateStore.TryGetClientId(certificate, out uint clientId));
        Assert.AreEqual((uint)123, clientId);
    }

    [TestMethod]
    public void TrustedCertificateValidationRejectsNameMismatchAndWrongEnhancedKeyUsageTest()
    {
        SecureSocketConnection.Configure(CreateSecurityConfig());
        using var certificate = LocalCertificateStore.GetOrCreate("SocketTestsTrustedCertificate");

        Assert.IsTrue(SecureSocketConnection.IsTrustedLocalCertificate(
            certificate,
            SecureSocketConnection.ServerAuthenticationEkuOid,
            SslPolicyErrors.None,
            rejectNameMismatch: true));
        Assert.IsFalse(SecureSocketConnection.IsTrustedLocalCertificate(
            certificate,
            SecureSocketConnection.ServerAuthenticationEkuOid,
            SslPolicyErrors.RemoteCertificateNameMismatch,
            rejectNameMismatch: true));
        Assert.IsFalse(SecureSocketConnection.IsTrustedLocalCertificate(
            certificate,
            "1.2.3.4",
            SslPolicyErrors.None,
            rejectNameMismatch: true));
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
            EnforceClientCertificateId = true,
            CertificatePasswordEnvironmentVariable = TestPasswordVariable,
            CertificateRenewBeforeDays = 15,
            RootCertificateLifetimeYears = 8,
            ModuleCertificateLifetimeYears = 3,
            AuthenticationTimeoutMilliseconds = 2500
        });

        Assert.AreEqual(SslProtocols.Tls13, SecureSocketConnection.ConfiguredProtocols);
        Assert.AreEqual(SocketSecurityProfile.EndToEndTls, SecureSocketConnection.SecurityProfile);
        Assert.AreEqual(SocketTransportSecurityMode.Tls, SecureSocketConnection.ConfiguredTransportMode);
        Assert.IsTrue(SecureSocketConnection.RequireTls13);
        Assert.IsTrue(SecureSocketConnection.RequireClientCertificate);
        Assert.IsTrue(SecureSocketConnection.EnforceClientCertificateId);
        Assert.AreEqual(2500, SecureSocketConnection.AuthenticationTimeoutMilliseconds);

        SecureSocketConnection.Configure(CreateSecurityConfig());
    }

    [TestMethod]
    public void SecurityProfileAppliesClientCertificateRequirementTest()
    {
        SecureSocketConnection.Configure(new SocketSecurityConfig
        {
            Profile = "EndToEndTls",
            TransportMode = "Tls",
            TlsProtocol = "Tls13",
            RequireClientCertificate = false
        });

        Assert.IsFalse(SecureSocketConnection.RequireClientCertificate);
        Assert.IsFalse(SecureSocketConnection.EnforceClientCertificateId);

        SecureSocketConnection.Configure(new SocketSecurityConfig
        {
            Profile = "EdgeTerminated",
            TlsProtocol = "None",
            RequireTls13 = false,
            RequireClientCertificate = true,
            TrustedNetwork = true
        });

        Assert.IsFalse(SecureSocketConnection.RequireClientCertificate);
        Assert.IsFalse(SecureSocketConnection.EnforceClientCertificateId);

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
    public async Task MessageEncryptionTransportProtectsFramesWithoutTlsTest()
    {
        Environment.SetEnvironmentVariable(TestMessageSecretVariable, Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        try
        {
            SecureSocketConnection.Configure(new SocketSecurityConfig
            {
                Profile = "EdgeTerminated",
                TransportMode = "MessageEncryption",
                TlsProtocol = "None",
                RequireTls13 = false,
                TrustedNetwork = true,
                MessageEncryptionSecretEnvironmentVariable = TestMessageSecretVariable
            });

            using Socket listener = SocketFactory.CreateTcpSocket();
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(SocketFactory.ListenBacklog);

            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            Socket clientSocket = SocketFactory.CreateTcpSocket();
            Task connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
            Socket serverSocket = await listener.AcceptAsync();
            await connectTask;

            Task<SecureSocketConnection> serverTask =
                SecureSocketConnection.AuthenticateServerAsync(serverSocket, "SocketTestsMessageServer");
            Task<SecureSocketConnection> clientTask =
                SecureSocketConnection.AuthenticateClientAsync(clientSocket, "SocketTestsMessageClient");
            await Task.WhenAll(serverTask, clientTask);

            using SecureSocketConnection server = await serverTask;
            using SecureSocketConnection client = await clientTask;

            Assert.AreEqual(SocketTransportSecurityMode.MessageEncryption, server.TransportMode);
            Assert.AreEqual(SocketTransportSecurityMode.MessageEncryption, client.TransportMode);
            Assert.AreEqual(SslProtocols.None, server.NegotiatedProtocol);
            Assert.AreEqual(SslProtocols.None, client.NegotiatedProtocol);

            int rentedBeforeDataPlane = SocketAsyncEventArgsFactory.RentedCount;
            int inUseBeforeDataPlane = SocketAsyncEventArgsFactory.InUseCount;

            Assert.IsTrue(await HelloWorldProtocol.SendAsync(client, HelloWorldProtocol.CreateRequest(77)));
            (bool requestReceived, HelloWorldRequest request) = await HelloWorldProtocol.TryReceiveRequestAsync(server);
            Assert.IsTrue(requestReceived);
            Assert.AreEqual((uint)77, request.ClientId);

            Assert.IsTrue(await HelloWorldProtocol.SendAsync(server, HelloWorldProtocol.CreateResponse(request.ClientId)));
            (bool responseReceived, HelloWorldResponse response) = await HelloWorldProtocol.TryReceiveResponseAsync(client);
            Assert.IsTrue(responseReceived);
            Assert.AreEqual((uint)77, response.ClientId);
            Assert.AreEqual(HelloWorldProtocol.DefaultMessage, response.Message);
            Assert.IsTrue(SocketAsyncEventArgsFactory.RentedCount >= rentedBeforeDataPlane + 4);
            Assert.AreEqual(inUseBeforeDataPlane, SocketAsyncEventArgsFactory.InUseCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestMessageSecretVariable, null);
            SecureSocketConnection.Configure(CreateSecurityConfig());
        }
    }

    [TestMethod]
    public void EdgeTerminatedProfileSelectsMessageEncryptionTransportTest()
    {
        SecureSocketConnection.Configure(new SocketSecurityConfig
        {
            Profile = "EdgeTerminated",
            TlsProtocol = "None",
            RequireTls13 = false,
            TrustedNetwork = true,
            MessageEncryptionSecretEnvironmentVariable = TestMessageSecretVariable
        });

        Assert.AreEqual(SocketSecurityProfile.EdgeTerminated, SecureSocketConnection.SecurityProfile);
        Assert.AreEqual(SocketTransportSecurityMode.MessageEncryption, SecureSocketConnection.ConfiguredTransportMode);

        SecureSocketConnection.Configure(CreateSecurityConfig());
    }

    [TestMethod]
    public void EdgeTerminatedProfileRequiresTrustedNetworkTest()
    {
        Assert.ThrowsException<InvalidOperationException>(() => SecureSocketConnection.Configure(new SocketSecurityConfig
        {
            Profile = "EdgeTerminated",
            TlsProtocol = "None",
            RequireTls13 = false,
            TrustedNetwork = false
        }));

        SecureSocketConnection.Configure(CreateSecurityConfig());
    }

    [TestMethod]
    public void EndToEndTlsProfileRejectsMessageEncryptionTransportTest()
    {
        Assert.ThrowsException<InvalidOperationException>(() => SecureSocketConnection.Configure(new SocketSecurityConfig
        {
            Profile = "EndToEndTls",
            TransportMode = "MessageEncryption",
            TlsProtocol = "None",
            RequireTls13 = false,
            TrustedNetwork = true
        }));

        SecureSocketConnection.Configure(CreateSecurityConfig());
    }

    [TestMethod]
    public void AppTokenSessionProfileFailsFastUntilSessionKeyHandshakeIsImplementedTest()
    {
        NotSupportedException exception = Assert.ThrowsException<NotSupportedException>(() =>
            SecureSocketConnection.Configure(new SocketSecurityConfig
            {
                Profile = "AppTokenSession",
                TransportMode = "MessageEncryption",
                TlsProtocol = "None",
                RequireTls13 = false,
                TrustedNetwork = true
            }));

        StringAssert.Contains(exception.Message, "planned but not implemented");
        SecureSocketConnection.Configure(CreateSecurityConfig());
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

    [TestMethod]
    public void CachedCertificateRemainsUsableAfterCacheInvalidationTest()
    {
        string directory = CreateTemporaryCertificateDirectory();
        Environment.SetEnvironmentVariable(TestPasswordVariable, $"password-{Guid.NewGuid():N}");
        X509Certificate2? cachedCertificate = null;
        try
        {
            SecureSocketConnection.Configure(CreateSecurityConfig(directory));
            cachedCertificate = LocalCertificateStore.GetOrCreateCached("SocketTestsCachedLifetime");

            SecureSocketConnection.Configure(CreateSecurityConfig(directory, certificateRenewBeforeDays: 800));

            using ECDsa? privateKey = cachedCertificate.GetECDsaPrivateKey();
            Assert.IsNotNull(privateKey);
            Assert.IsTrue(cachedCertificate.HasPrivateKey);
        }
        finally
        {
            cachedCertificate?.Dispose();
            Environment.SetEnvironmentVariable(TestPasswordVariable, null);
            DeleteTemporaryCertificateDirectory(directory);
            SecureSocketConnection.Configure(CreateSecurityConfig());
        }
    }

    [TestMethod]
    public async Task CachedCertificatesCanBeCreatedConcurrentlyForDifferentClientsTest()
    {
        string directory = CreateTemporaryCertificateDirectory();
        Environment.SetEnvironmentVariable(TestPasswordVariable, $"password-{Guid.NewGuid():N}");
        try
        {
            SecureSocketConnection.Configure(CreateSecurityConfig(directory, requireClientCertificate: true));
            int clientCount = 12;
            Task<X509Certificate2>[] tasks = new Task<X509Certificate2>[clientCount];
            for (int index = 0; index < clientCount; index++)
            {
                int clientId = 2000 + index;
                tasks[index] = Task.Run(() => LocalCertificateStore.GetOrCreateCached($"SocketClient-{clientId}"));
            }

            X509Certificate2[] certificates = await Task.WhenAll(tasks);

            for (int index = 0; index < certificates.Length; index++)
            {
                Assert.IsTrue(LocalCertificateStore.TryGetClientId(certificates[index], out uint clientId));
                Assert.AreEqual((uint)(2000 + index), clientId);
                Assert.IsTrue(File.Exists(LocalCertificateStore.GetCertificatePath($"SocketClient-{clientId}")));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestPasswordVariable, null);
            DeleteTemporaryCertificateDirectory(directory);
            SecureSocketConnection.Configure(CreateSecurityConfig());
        }
    }

    [TestMethod]
    public async Task CachedCertificateCreationIsSingleFlightForSameClientTest()
    {
        string directory = CreateTemporaryCertificateDirectory();
        Environment.SetEnvironmentVariable(TestPasswordVariable, $"password-{Guid.NewGuid():N}");
        try
        {
            SecureSocketConnection.Configure(CreateSecurityConfig(directory, requireClientCertificate: true));
            int workerCount = 12;
            Task<X509Certificate2>[] tasks = new Task<X509Certificate2>[workerCount];
            for (int index = 0; index < workerCount; index++)
            {
                tasks[index] = Task.Run(() => LocalCertificateStore.GetOrCreateCached("SocketClient-3000"));
            }

            X509Certificate2[] certificates = await Task.WhenAll(tasks);
            string expectedSerialNumber = Convert.ToHexString(certificates[0].GetSerialNumber());

            foreach (X509Certificate2 certificate in certificates)
            {
                Assert.IsTrue(LocalCertificateStore.TryGetClientId(certificate, out uint clientId));
                Assert.AreEqual((uint)3000, clientId);
                Assert.AreEqual(expectedSerialNumber, Convert.ToHexString(certificate.GetSerialNumber()));
            }
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
            TransportMode = "Tls",
            TlsProtocol = "Auto",
            RequireTls13 = false,
            RequireClientCertificate = requireClientCertificate,
            CertificateDirectory = certificateDirectory,
            CertificatePasswordEnvironmentVariable = TestPasswordVariable,
            CertificateRenewBeforeDays = certificateRenewBeforeDays,
            RootCertificateLifetimeYears = 10,
            ModuleCertificateLifetimeYears = 2,
            AuthenticationTimeoutMilliseconds = 30000
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
