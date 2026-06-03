using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using SocketCommon.Configuration;

namespace SocketCommon.Model;

public sealed class SecureSocketConnection : IDisposable
{
    public const string LocalCertificateSubject = "CN=SocketServerLocal";
    public const string LocalRootCertificateSubject = "CN=SocketServerLocalRootCA";
    public const string TargetHost = "SocketServerLocal";
    public const string CertificateDirectoryEnvironmentVariable = "SOCKET_CERTIFICATE_DIR";
    public const string RequireTls13EnvironmentVariable = "SOCKET_REQUIRE_TLS13";

    private static readonly object OptionsLock = new();
    private static SslProtocols configuredProtocols = SslProtocols.None;
    private static bool requireTls13;
    private static bool requireClientCertificate;
    private static int authenticationTimeoutMilliseconds = 5000;
    private static string certificateDirectory = "";
    private static string certificatePasswordEnvironmentVariable = "SOCKET_CERTIFICATE_PASSWORD";
    private static int certificateRenewBeforeDays = 30;
    private static int rootCertificateLifetimeYears = 10;
    private static int moduleCertificateLifetimeYears = 2;

    private readonly NetworkStream networkStream;
    private readonly SslStream sslStream;
    private bool disposed;

    private SecureSocketConnection(Socket socket, NetworkStream networkStream, SslStream sslStream, string moduleName)
    {
        this.Socket = socket;
        this.networkStream = networkStream;
        this.sslStream = sslStream;
        this.ModuleName = moduleName;
    }

    public Socket Socket { get; }

    public string ModuleName { get; }

    public SslProtocols NegotiatedProtocol => this.sslStream.SslProtocol;

    public bool IsConnected => !this.disposed && this.Socket.Connected;

    public static SslProtocols ConfiguredProtocols
    {
        get
        {
            lock (OptionsLock)
            {
                return configuredProtocols;
            }
        }
    }

    public static bool RequireTls13
    {
        get
        {
            lock (OptionsLock)
            {
                return requireTls13;
            }
        }
    }

    public static bool RequireClientCertificate
    {
        get
        {
            lock (OptionsLock)
            {
                return requireClientCertificate;
            }
        }
    }

    public static int AuthenticationTimeoutMilliseconds
    {
        get
        {
            lock (OptionsLock)
            {
                return authenticationTimeoutMilliseconds;
            }
        }
    }

    public static void Configure(SocketSecurityConfig config)
    {
        if (config == null)
        {
            return;
        }

        lock (OptionsLock)
        {
            configuredProtocols = ParseProtocols(config.TlsProtocol);
            requireTls13 = config.RequireTls13;
            requireClientCertificate = config.RequireClientCertificate;
            authenticationTimeoutMilliseconds = Math.Max(1000, config.AuthenticationTimeoutMilliseconds);
            certificateDirectory = config.CertificateDirectory?.Trim() ?? "";
            certificatePasswordEnvironmentVariable = string.IsNullOrWhiteSpace(config.CertificatePasswordEnvironmentVariable)
                ? "SOCKET_CERTIFICATE_PASSWORD"
                : config.CertificatePasswordEnvironmentVariable.Trim();
            certificateRenewBeforeDays = Math.Max(0, config.CertificateRenewBeforeDays);
            rootCertificateLifetimeYears = Math.Max(1, config.RootCertificateLifetimeYears);
            moduleCertificateLifetimeYears = Math.Max(1, config.ModuleCertificateLifetimeYears);
        }
    }

    public static async Task<SecureSocketConnection> AuthenticateClientAsync(Socket socket, string moduleName)
    {
        NetworkStream networkStream = new(socket, ownsSocket: true);
        SslStream sslStream = new(
            networkStream,
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) => IsTrustedLocalCertificate(certificate));

        await WaitForAuthenticationAsync(sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = TargetHost,
            EnabledSslProtocols = ConfiguredProtocols,
            ClientCertificates = GetClientCertificates(moduleName),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }));
        EnsureTls13IfRequired(sslStream);

        return new SecureSocketConnection(socket, networkStream, sslStream, moduleName);
    }

    public static async Task<SecureSocketConnection> AuthenticateServerAsync(Socket socket, string moduleName)
    {
        X509Certificate2 certificate = LocalCertificateStore.GetOrCreate(moduleName);
        NetworkStream networkStream = new(socket, ownsSocket: true);
        SslStream sslStream = new(networkStream, leaveInnerStreamOpen: false);

        await WaitForAuthenticationAsync(sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = ConfiguredProtocols,
            ClientCertificateRequired = RequireClientCertificate,
            RemoteCertificateValidationCallback = RequireClientCertificate
                ? (_, clientCertificate, _, _) => IsTrustedLocalCertificate(clientCertificate)
                : null,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }));
        EnsureTls13IfRequired(sslStream);

        return new SecureSocketConnection(socket, networkStream, sslStream, moduleName);
    }

    public async Task<bool> SendAsync(byte[] bytes)
    {
        if (bytes == null || this.disposed)
        {
            return false;
        }

        try
        {
            await this.sslStream.WriteAsync(bytes.AsMemory(0, bytes.Length));
            await this.sslStream.FlushAsync();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (AuthenticationException)
        {
            return false;
        }
    }

    public async Task<byte[]> ReceiveExactAsync(int length)
    {
        if (length < 0 || this.disposed)
        {
            return null;
        }

        byte[] buffer = new byte[length];
        int offset = 0;
        try
        {
            while (offset < length)
            {
                int read = await this.sslStream.ReadAsync(buffer.AsMemory(offset, length - offset));
                if (read <= 0)
                {
                    return null;
                }

                offset += read;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        return buffer;
    }

    public void Close()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        try
        {
            if (this.Socket.Connected)
            {
                this.Socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.sslStream.Dispose();
            this.networkStream.Dispose();
        }
    }

    public void Dispose()
    {
        this.Close();
    }

    private static bool IsTrustedLocalCertificate(X509Certificate certificate)
    {
        if (certificate == null)
        {
            return false;
        }

        using X509Certificate2 certificate2 = new(certificate);
        if (!certificate2.Subject.Contains(LocalCertificateSubject, StringComparison.Ordinal) ||
            !LocalCertificateStore.HasSubjectAlternativeName(certificate2))
        {
            return false;
        }

        using X509Certificate2 rootCertificate = LocalCertificateStore.GetOrCreateRootAuthority();
        return LocalCertificateStore.IsSignedByRoot(certificate2, rootCertificate);
    }

    private static X509CertificateCollection GetClientCertificates(string moduleName)
    {
        if (!RequireClientCertificate)
        {
            return null;
        }

        X509CertificateCollection certificates = new()
        {
            LocalCertificateStore.GetOrCreate(moduleName)
        };
        return certificates;
    }

    private static async Task WaitForAuthenticationAsync(Task authenticationTask)
    {
        Task completedTask = await Task.WhenAny(
            authenticationTask,
            Task.Delay(AuthenticationTimeoutMilliseconds));
        if (completedTask != authenticationTask)
        {
            throw new AuthenticationException("TLS 1.3 authentication timed out.");
        }

        await authenticationTask;
    }

    private static void EnsureTls13IfRequired(SslStream sslStream)
    {
        if ((RequireTls13 || Environment.GetEnvironmentVariable(RequireTls13EnvironmentVariable) == "true") &&
            sslStream.SslProtocol != SslProtocols.Tls13)
        {
            throw new AuthenticationException($"TLS 1.3 is required. negotiated={sslStream.SslProtocol}");
        }
    }

    private static SslProtocols ParseProtocols(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol) ||
            string.Equals(protocol, "Auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(protocol, "None", StringComparison.OrdinalIgnoreCase))
        {
            return SslProtocols.None;
        }

        SslProtocols result = SslProtocols.None;
        foreach (string token in protocol.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse(token, ignoreCase: true, out SslProtocols parsed))
            {
                result |= parsed;
            }
        }

        return result == SslProtocols.None ? SslProtocols.Tls13 : result;
    }

    internal static string ConfiguredCertificateDirectory
    {
        get
        {
            lock (OptionsLock)
            {
                return certificateDirectory;
            }
        }
    }

    internal static string CertificatePasswordEnvironmentVariable
    {
        get
        {
            lock (OptionsLock)
            {
                return certificatePasswordEnvironmentVariable;
            }
        }
    }

    internal static int CertificateRenewBeforeDays
    {
        get
        {
            lock (OptionsLock)
            {
                return certificateRenewBeforeDays;
            }
        }
    }

    internal static int RootCertificateLifetimeYears
    {
        get
        {
            lock (OptionsLock)
            {
                return rootCertificateLifetimeYears;
            }
        }
    }

    internal static int ModuleCertificateLifetimeYears
    {
        get
        {
            lock (OptionsLock)
            {
                return moduleCertificateLifetimeYears;
            }
        }
    }
}

public static class LocalCertificateStore
{
    private const string RootCertificateFileName = "SocketServerLocalRootCA.pfx";

    public static X509Certificate2 GetOrCreate(string moduleName)
    {
        string path = GetCertificatePath(moduleName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using X509Certificate2 rootCertificate = GetOrCreateRootAuthority();

        if (File.Exists(path))
        {
            X509Certificate2 existingCertificate = TryLoad(path);
            if (existingCertificate != null && IsModuleCertificateValid(existingCertificate, rootCertificate))
            {
                return existingCertificate;
            }

            if (existingCertificate != null)
            {
                existingCertificate.Dispose();
            }

            File.Delete(path);
        }

        if (!File.Exists(path))
        {
            using X509Certificate2 certificate = CreateModuleCertificate(moduleName, rootCertificate);
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, GetCertificatePassword()));
        }

        return Load(path);
    }

    public static X509Certificate2 GetOrCreateRootAuthority()
    {
        string path = GetRootCertificatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            X509Certificate2 existingCertificate = TryLoad(path);
            if (existingCertificate != null && IsRootCertificateValid(existingCertificate))
            {
                return existingCertificate;
            }

            if (existingCertificate != null)
            {
                existingCertificate.Dispose();
            }

            File.Delete(path);
        }

        using X509Certificate2 certificate = CreateRootAuthorityCertificate();
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, GetCertificatePassword()));
        return Load(path);
    }

    private static X509Certificate2 Load(string path)
    {
        return X509CertificateLoader.LoadPkcs12FromFile(
            path,
            GetCertificatePassword(),
            X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 TryLoad(string path)
    {
        try
        {
            return Load(path);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string GetCertificatePassword()
    {
        string variableName = SecureSocketConnection.CertificatePasswordEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return "";
        }

        return Environment.GetEnvironmentVariable(variableName) ?? "";
    }

    public static string GetCertificateDirectory()
    {
        string configuredSocketDirectory = SecureSocketConnection.ConfiguredCertificateDirectory;
        if (!string.IsNullOrWhiteSpace(configuredSocketDirectory))
        {
            return configuredSocketDirectory;
        }

        string configuredDirectory = Environment.GetEnvironmentVariable(
            SecureSocketConnection.CertificateDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return configuredDirectory;
        }

        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SocketServer.sln")))
            {
                return Path.Combine(directory.FullName, "Certificates");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "Certificates");
    }

    public static string GetRootCertificatePath()
    {
        return Path.Combine(GetCertificateDirectory(), RootCertificateFileName);
    }

    public static string GetCertificatePath(string moduleName)
    {
        string safeModuleName = string.IsNullOrWhiteSpace(moduleName)
            ? "SocketCommon"
            : moduleName.Trim();
        return Path.Combine(GetCertificateDirectory(), $"{safeModuleName}.pfx");
    }

    private static X509Certificate2 CreateRootAuthorityCertificate()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(
            SecureSocketConnection.LocalRootCertificateSubject,
            ecdsa,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(SecureSocketConnection.RootCertificateLifetimeYears));
    }

    private static X509Certificate2 CreateModuleCertificate(string moduleName, X509Certificate2 rootCertificate)
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(
            SecureSocketConnection.LocalCertificateSubject,
            ecdsa,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"),
                new("1.3.6.1.5.5.7.3.2")
            },
            false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        SubjectAlternativeNameBuilder subjectAlternativeNameBuilder = new();
        subjectAlternativeNameBuilder.AddDnsName(SecureSocketConnection.TargetHost);
        subjectAlternativeNameBuilder.AddDnsName("localhost");
        request.CertificateExtensions.Add(subjectAlternativeNameBuilder.Build());

        using X509Certificate2 certificate = request.Create(
            rootCertificate,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(SecureSocketConnection.ModuleCertificateLifetimeYears),
            CreateSerialNumber(moduleName));
        return certificate.CopyWithPrivateKey(ecdsa);
    }

    public static bool HasSubjectAlternativeName(X509Certificate2 certificate)
    {
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension.Oid?.Value == "2.5.29.17")
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsSignedByRoot(X509Certificate2 certificate, X509Certificate2 rootCertificate)
    {
        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(rootCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = DateTime.UtcNow;
        return chain.Build(certificate);
    }

    private static bool IsModuleCertificateValid(X509Certificate2 certificate, X509Certificate2 rootCertificate)
    {
        return certificate.GetECDsaPrivateKey() != null &&
            certificate.Subject.Contains(SecureSocketConnection.LocalCertificateSubject, StringComparison.Ordinal) &&
            IsWithinRotationWindow(certificate) &&
            HasSubjectAlternativeName(certificate) &&
            IsSignedByRoot(certificate, rootCertificate);
    }

    private static bool IsRootCertificateValid(X509Certificate2 certificate)
    {
        return certificate.GetECDsaPrivateKey() != null &&
            certificate.Subject.Contains(SecureSocketConnection.LocalRootCertificateSubject, StringComparison.Ordinal) &&
            IsWithinRotationWindow(certificate) &&
            IsCertificateAuthority(certificate);
    }

    private static bool IsWithinRotationWindow(X509Certificate2 certificate)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset renewAt = now.AddDays(SecureSocketConnection.CertificateRenewBeforeDays);
        return certificate.NotBefore <= now.UtcDateTime && certificate.NotAfter > renewAt.UtcDateTime;
    }

    private static bool IsCertificateAuthority(X509Certificate2 certificate)
    {
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension is X509BasicConstraintsExtension basicConstraints)
            {
                return basicConstraints.CertificateAuthority;
            }
        }

        return false;
    }

    private static byte[] CreateSerialNumber(string moduleName)
    {
        byte[] serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        serialNumber[0] &= 0x7F;
        return serialNumber;
    }
}
