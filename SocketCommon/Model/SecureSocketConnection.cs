using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Configuration;

namespace SocketCommon.Model;

public enum SocketTransportSecurityMode
{
    Tls,
    MessageEncryption
}

public enum SocketSecurityProfile
{
    EndToEndTls,
    EdgeTerminated,
    AppTokenSession
}

public sealed class SecureSocketConnection : IDisposable
{
    public const string LocalCertificateSubject = "CN=SocketServerLocal";
    public const string LocalRootCertificateSubject = "CN=SocketServerLocalRootCA";
    public const string TargetHost = "SocketServerLocal";
    public const string CertificateDirectoryEnvironmentVariable = "SOCKET_CERTIFICATE_DIR";
    public const string RequireTls13EnvironmentVariable = "SOCKET_REQUIRE_TLS13";
    public const string DefaultMessageEncryptionSecretEnvironmentVariable = "SOCKET_MESSAGE_SECRET";
    internal const string ServerAuthenticationEkuOid = "1.3.6.1.5.5.7.3.1";
    internal const string ClientAuthenticationEkuOid = "1.3.6.1.5.5.7.3.2";

    private static readonly object OptionsLock = new();
    private static SocketSecurityProfile securityProfile = SocketSecurityProfile.EndToEndTls;
    private static SocketTransportSecurityMode transportMode = SocketTransportSecurityMode.Tls;
    private static SslProtocols configuredProtocols = SslProtocols.None;
    private static bool requireTls13;
    private static bool requireClientCertificate;
    private static bool enforceClientCertificateId;
    private static int authenticationTimeoutMilliseconds = 30000;
    private static string certificateDirectory = "";
    private static string certificatePasswordEnvironmentVariable = "SOCKET_CERTIFICATE_PASSWORD";
    private static int certificateRenewBeforeDays = 30;
    private static int rootCertificateLifetimeYears = 10;
    private static int moduleCertificateLifetimeYears = 2;
    private static string messageEncryptionSecretEnvironmentVariable = DefaultMessageEncryptionSecretEnvironmentVariable;

    private readonly NetworkStream networkStream;
    private readonly SslStream sslStream;
    private readonly SocketMessageProtector messageProtector;
    private byte[] pendingPlainFrameBytes;
    private int pendingPlainFrameOffset;
    private bool disposed;

    private SecureSocketConnection(
        Socket socket,
        NetworkStream networkStream,
        SslStream sslStream,
        SocketMessageProtector messageProtector,
        string moduleName,
        uint? remoteCertificateClientId = null)
    {
        this.Socket = socket;
        this.networkStream = networkStream;
        this.sslStream = sslStream;
        this.messageProtector = messageProtector;
        this.ModuleName = moduleName;
        this.RemoteCertificateClientId = remoteCertificateClientId;
    }

    public Socket Socket { get; }

    public string ModuleName { get; }

    public uint? RemoteCertificateClientId { get; }

    public SslProtocols NegotiatedProtocol => this.sslStream?.SslProtocol ?? SslProtocols.None;

    public SocketTransportSecurityMode TransportMode => this.messageProtector == null
        ? SocketTransportSecurityMode.Tls
        : SocketTransportSecurityMode.MessageEncryption;

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

    public static SocketTransportSecurityMode ConfiguredTransportMode
    {
        get
        {
            lock (OptionsLock)
            {
                return transportMode;
            }
        }
    }

    public static SocketSecurityProfile SecurityProfile
    {
        get
        {
            lock (OptionsLock)
            {
                return securityProfile;
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

    public static bool EnforceClientCertificateId
    {
        get
        {
            lock (OptionsLock)
            {
                return enforceClientCertificateId;
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
            SocketSecurityProfile nextSecurityProfile = ParseSecurityProfile(config.Profile);
            ValidateSecurityProfile(config, nextSecurityProfile);
            SocketTransportSecurityMode nextTransportMode =
                ParseTransportMode(config.TransportMode, config.TlsProtocol, nextSecurityProfile);
            bool nextRequireClientCertificate =
                nextSecurityProfile == SocketSecurityProfile.EndToEndTls &&
                config.RequireClientCertificate;

            securityProfile = nextSecurityProfile;
            configuredProtocols = ParseProtocols(config.TlsProtocol);
            transportMode = nextTransportMode;
            requireTls13 = config.RequireTls13;
            requireClientCertificate = nextRequireClientCertificate;
            enforceClientCertificateId = nextRequireClientCertificate && config.EnforceClientCertificateId;
            authenticationTimeoutMilliseconds = Math.Max(1000, config.AuthenticationTimeoutMilliseconds);
            certificateDirectory = config.CertificateDirectory?.Trim() ?? "";
            certificatePasswordEnvironmentVariable = string.IsNullOrWhiteSpace(config.CertificatePasswordEnvironmentVariable)
                ? "SOCKET_CERTIFICATE_PASSWORD"
                : config.CertificatePasswordEnvironmentVariable.Trim();
            certificateRenewBeforeDays = Math.Max(0, config.CertificateRenewBeforeDays);
            rootCertificateLifetimeYears = Math.Max(1, config.RootCertificateLifetimeYears);
            moduleCertificateLifetimeYears = Math.Max(1, config.ModuleCertificateLifetimeYears);
            messageEncryptionSecretEnvironmentVariable = string.IsNullOrWhiteSpace(config.MessageEncryptionSecretEnvironmentVariable)
                ? DefaultMessageEncryptionSecretEnvironmentVariable
                : config.MessageEncryptionSecretEnvironmentVariable.Trim();
        }

        LocalCertificateStore.ClearCertificateCaches();
    }

    public static async Task<SecureSocketConnection> AuthenticateClientAsync(Socket socket, string moduleName)
    {
        NetworkStream networkStream = new(socket, ownsSocket: true);
        if (ConfiguredTransportMode == SocketTransportSecurityMode.MessageEncryption)
        {
            return new SecureSocketConnection(
                socket,
                networkStream,
                null,
                CreateMessageProtector(),
                moduleName);
        }

        SslStream sslStream = new(
            networkStream,
            leaveInnerStreamOpen: false,
            (_, certificate, _, sslPolicyErrors) => IsTrustedLocalCertificate(
                certificate,
                ServerAuthenticationEkuOid,
                sslPolicyErrors,
                rejectNameMismatch: true));

        await WaitForAuthenticationAsync(sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = TargetHost,
            EnabledSslProtocols = ConfiguredProtocols,
            ClientCertificateContext = GetClientCertificateContext(moduleName),
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }));
        EnsureTls13IfRequired(sslStream);

        return new SecureSocketConnection(socket, networkStream, sslStream, null, moduleName);
    }

    public static async Task<SecureSocketConnection> AuthenticateServerAsync(Socket socket, string moduleName)
    {
        NetworkStream networkStream = new(socket, ownsSocket: true);
        if (ConfiguredTransportMode == SocketTransportSecurityMode.MessageEncryption)
        {
            return new SecureSocketConnection(
                socket,
                networkStream,
                null,
                CreateMessageProtector(),
                moduleName);
        }

        SslStream sslStream = new(networkStream, leaveInnerStreamOpen: false);

        SslServerAuthenticationOptions options = new()
        {
            ServerCertificateContext = LocalCertificateStore.GetOrCreateCertificateContext(moduleName),
            EnabledSslProtocols = ConfiguredProtocols,
            ClientCertificateRequired = RequireClientCertificate,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        };
        if (RequireClientCertificate)
        {
            options.RemoteCertificateValidationCallback =
                (_, clientCertificate, _, sslPolicyErrors) => clientCertificate != null &&
                    IsTrustedLocalCertificate(
                        clientCertificate,
                        ClientAuthenticationEkuOid,
                        sslPolicyErrors,
                        rejectNameMismatch: false);
        }

        await WaitForAuthenticationAsync(sslStream.AuthenticateAsServerAsync(options));
        EnsureTls13IfRequired(sslStream);

        uint? remoteCertificateClientId = LocalCertificateStore.TryGetClientId(
            sslStream.RemoteCertificate,
            out uint clientId)
            ? clientId
            : null;

        return new SecureSocketConnection(socket, networkStream, sslStream, null, moduleName, remoteCertificateClientId);
    }

    public async Task<bool> SendAsync(byte[] bytes)
    {
        if (bytes == null || this.disposed)
        {
            return false;
        }

        try
        {
            if (this.messageProtector != null)
            {
                if (!SocketMessageFrame.TryDecode(bytes, out SocketMessageFrame frame))
                {
                    return false;
                }

                SocketMessageFrame protectedFrame = this.messageProtector.Protect(frame);
                byte[] protectedBytes = protectedFrame.Encode();
                return await SocketAsyncEventArgsTransport.SendAsync(this.Socket, protectedBytes);
            }

            await this.WriteWithTimeoutAsync(this.sslStream, bytes);
            return true;
        }
        catch (OperationCanceledException)
        {
            this.Close();
            return false;
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
        return await this.ReceiveExactAsync(length, SocketFactory.ReadTimeoutMilliseconds);
    }

    public async Task<byte[]> ReceiveExactAsync(int length, int timeoutMilliseconds)
    {
        if (length < 0 || this.disposed)
        {
            return null;
        }

        timeoutMilliseconds = NormalizeTimeoutMilliseconds(timeoutMilliseconds);
        if (this.messageProtector != null)
        {
            return await this.ReceiveExactProtectedAsync(length, timeoutMilliseconds);
        }

        return await this.ReceiveExactFromSslAsync(length, timeoutMilliseconds);
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
            this.sslStream?.Dispose();
            this.networkStream.Dispose();
        }
    }

    public void Dispose()
    {
        this.Close();
    }

    private async Task<byte[]> ReceiveExactFromSslAsync(int length, int timeoutMilliseconds)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        try
        {
            using CancellationTokenSource cancellation = new(timeoutMilliseconds);
            while (offset < length)
            {
                int read = await this.sslStream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellation.Token);
                if (read <= 0)
                {
                    return null;
                }

                offset += read;
            }
        }
        catch (OperationCanceledException)
        {
            this.Close();
            return null;
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

    private async Task<byte[]> ReceiveExactFromNetworkAsync(int length, int timeoutMilliseconds)
    {
        try
        {
            byte[] buffer = await SocketAsyncEventArgsTransport.ReceiveExactAsync(
                this.Socket,
                length,
                timeoutMilliseconds);
            if (buffer == null)
            {
                this.Close();
            }

            return buffer;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

    }

    private async Task<byte[]> ReceiveExactProtectedAsync(int length, int timeoutMilliseconds)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        if (this.pendingPlainFrameBytes == null || this.pendingPlainFrameOffset >= this.pendingPlainFrameBytes.Length)
        {
            if (!await this.ReadProtectedFrameAsync(timeoutMilliseconds))
            {
                return null;
            }
        }

        if (this.pendingPlainFrameBytes == null ||
            this.pendingPlainFrameOffset + length > this.pendingPlainFrameBytes.Length)
        {
            return null;
        }

        byte[] result = new byte[length];
        Buffer.BlockCopy(this.pendingPlainFrameBytes, this.pendingPlainFrameOffset, result, 0, length);
        this.pendingPlainFrameOffset += length;
        if (this.pendingPlainFrameOffset >= this.pendingPlainFrameBytes.Length)
        {
            this.pendingPlainFrameBytes = null;
            this.pendingPlainFrameOffset = 0;
        }

        return result;
    }

    private async Task<bool> ReadProtectedFrameAsync(int timeoutMilliseconds)
    {
        byte[] header = await this.ReceiveExactFromNetworkAsync(SocketMessageFrame.HeaderLength, timeoutMilliseconds);
        if (header == null)
        {
            return false;
        }

        uint protectedPayloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (protectedPayloadLength > SocketMessageProtector.MaxProtectedPayloadLength)
        {
            return false;
        }

        byte[] protectedPayload = protectedPayloadLength == 0
            ? Array.Empty<byte>()
            : await this.ReceiveExactFromNetworkAsync((int)protectedPayloadLength, timeoutMilliseconds);
        if (protectedPayload == null)
        {
            return false;
        }

        SocketMessageFrame protectedFrame = new(
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)),
            protectedPayload,
            skipPayloadLimitValidation: true);
        if (!this.messageProtector.TryUnprotect(protectedFrame, out SocketMessageFrame plainFrame))
        {
            return false;
        }

        this.pendingPlainFrameBytes = plainFrame.Encode();
        this.pendingPlainFrameOffset = 0;
        return true;
    }

    private async Task WriteWithTimeoutAsync(Stream stream, byte[] bytes)
    {
        using CancellationTokenSource cancellation = new(SocketFactory.WriteTimeoutMilliseconds);
        await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellation.Token);
        await stream.FlushAsync(cancellation.Token);
    }

    private static int NormalizeTimeoutMilliseconds(int timeoutMilliseconds)
    {
        return timeoutMilliseconds > 0 ? timeoutMilliseconds : SocketFactory.ReadTimeoutMilliseconds;
    }

    internal static bool IsTrustedLocalCertificate(
        X509Certificate certificate,
        string requiredEnhancedKeyUsageOid,
        SslPolicyErrors sslPolicyErrors = SslPolicyErrors.None,
        bool rejectNameMismatch = false)
    {
        if (certificate == null)
        {
            return false;
        }

        if (rejectNameMismatch && (sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
        {
            return false;
        }

        using X509Certificate2 certificate2 = new(certificate);
        if (!certificate2.Subject.Contains(LocalCertificateSubject, StringComparison.Ordinal) ||
            !LocalCertificateStore.HasSubjectAlternativeName(certificate2))
        {
            return false;
        }

        if (!LocalCertificateStore.HasEnhancedKeyUsage(certificate2, requiredEnhancedKeyUsageOid))
        {
            return false;
        }

        X509Certificate2 rootCertificate = LocalCertificateStore.GetCachedRootAuthority();
        return LocalCertificateStore.IsSignedByRoot(certificate2, rootCertificate);
    }

    private static SslStreamCertificateContext GetClientCertificateContext(string moduleName)
    {
        if (!RequireClientCertificate)
        {
            return null;
        }

        return LocalCertificateStore.GetOrCreateCertificateContext(moduleName);
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

    private static SocketSecurityProfile ParseSecurityProfile(string profile)
    {
        if (string.IsNullOrWhiteSpace(profile) ||
            string.Equals(profile, "EndToEndTls", StringComparison.OrdinalIgnoreCase))
        {
            return SocketSecurityProfile.EndToEndTls;
        }

        if (string.Equals(profile, "EdgeTerminated", StringComparison.OrdinalIgnoreCase))
        {
            return SocketSecurityProfile.EdgeTerminated;
        }

        if (string.Equals(profile, "AppTokenSession", StringComparison.OrdinalIgnoreCase))
        {
            return SocketSecurityProfile.AppTokenSession;
        }

        throw new InvalidOperationException($"Unknown security profile: {profile}");
    }

    private static void ValidateSecurityProfile(SocketSecurityConfig config, SocketSecurityProfile profile)
    {
        if (profile == SocketSecurityProfile.AppTokenSession)
        {
            throw new NotSupportedException("AppTokenSession security profile is planned but not implemented.");
        }

        if (profile == SocketSecurityProfile.EndToEndTls &&
            IsMessageEncryptionTransport(config.TransportMode, config.TlsProtocol))
        {
            throw new InvalidOperationException("EndToEndTls security profile requires TLS transport.");
        }

        if (profile == SocketSecurityProfile.EdgeTerminated)
        {
            if (!config.TrustedNetwork)
            {
                throw new InvalidOperationException("EdgeTerminated security profile requires trustedNetwork=true.");
            }

            if (string.Equals(config.TransportMode, "Tls", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("EdgeTerminated security profile cannot use Tls transportMode.");
            }
        }
    }

    private static SocketTransportSecurityMode ParseTransportMode(
        string transportModeValue,
        string tlsProtocol,
        SocketSecurityProfile profile)
    {
        if (profile == SocketSecurityProfile.EdgeTerminated)
        {
            return SocketTransportSecurityMode.MessageEncryption;
        }

        if (!string.IsNullOrWhiteSpace(transportModeValue))
        {
            if (string.Equals(transportModeValue, "Tls", StringComparison.OrdinalIgnoreCase))
            {
                return SocketTransportSecurityMode.Tls;
            }

            if (string.Equals(transportModeValue, "MessageEncryption", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(transportModeValue, "Encrypted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(transportModeValue, "PlainEncrypted", StringComparison.OrdinalIgnoreCase))
            {
                return SocketTransportSecurityMode.MessageEncryption;
            }
        }

        return string.Equals(tlsProtocol, "None", StringComparison.OrdinalIgnoreCase)
            ? SocketTransportSecurityMode.MessageEncryption
            : SocketTransportSecurityMode.Tls;
    }

    private static bool IsMessageEncryptionTransport(string transportModeValue, string tlsProtocol)
    {
        if (string.Equals(tlsProtocol, "None", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(transportModeValue, "MessageEncryption", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(transportModeValue, "Encrypted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(transportModeValue, "PlainEncrypted", StringComparison.OrdinalIgnoreCase);
    }

    private static SocketMessageProtector CreateMessageProtector()
    {
        string variableName;
        lock (OptionsLock)
        {
            variableName = messageEncryptionSecretEnvironmentVariable;
        }

        string secret = Environment.GetEnvironmentVariable(variableName) ?? "";
        try
        {
            return SocketMessageProtector.FromSecret(secret);
        }
        catch (InvalidOperationException exception)
        {
            throw new AuthenticationException("Message encryption secret is not configured.", exception);
        }
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
    private static readonly object RootAuthorityCacheLock = new();
    private static readonly object ModuleCertificateCacheLock = new();
    private static readonly Dictionary<string, object> ModuleCertificateLocks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CachedModuleCertificate> CachedModuleCertificates = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CachedCertificateContext> CachedCertificateContexts = new(StringComparer.Ordinal);
    private static X509Certificate2 cachedRootAuthority;
    private static string cachedRootAuthorityPath = "";
    private static string cachedRootAuthorityPassword = "";
    private static DateTime cachedRootAuthorityWriteTimeUtc;

    public static X509Certificate2 GetOrCreate(string moduleName)
    {
        string path = GetCertificatePath(moduleName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using X509Certificate2 rootCertificate = GetOrCreateRootAuthority();

        if (File.Exists(path))
        {
            X509Certificate2 existingCertificate = TryLoad(path);
            if (existingCertificate != null && IsModuleCertificateValid(existingCertificate, rootCertificate, moduleName))
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

    public static X509Certificate2 GetOrCreateCached(string moduleName)
    {
        string key = NormalizeModuleName(moduleName);
        string path = GetCertificatePath(key);
        string password = GetCertificatePassword();
        object moduleLock = GetModuleCertificateLock(key);

        lock (moduleLock)
        {
            X509Certificate2 rootCertificate = GetCachedRootAuthority();
            DateTime writeTimeUtc = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;

            lock (ModuleCertificateCacheLock)
            {
                if (CachedModuleCertificates.TryGetValue(key, out CachedModuleCertificate cached) &&
                    string.Equals(cached.Path, path, StringComparison.Ordinal) &&
                    string.Equals(cached.Password, password, StringComparison.Ordinal) &&
                    cached.WriteTimeUtc == writeTimeUtc &&
                    IsModuleCertificateValid(cached.Certificate, rootCertificate, key))
                {
                    return cached.Certificate;
                }

                CachedModuleCertificates.Remove(key);
            }

            X509Certificate2 certificate = GetOrCreate(key);
            lock (ModuleCertificateCacheLock)
            {
                CachedModuleCertificates[key] = new CachedModuleCertificate(
                    certificate,
                    path,
                    password,
                    File.GetLastWriteTimeUtc(path));
            }

            return certificate;
        }
    }

    public static SslStreamCertificateContext GetOrCreateCertificateContext(string moduleName)
    {
        string key = NormalizeModuleName(moduleName);
        string path = GetCertificatePath(key);
        string password = GetCertificatePassword();
        object moduleLock = GetModuleCertificateLock(key);

        lock (moduleLock)
        {
            X509Certificate2 certificate = GetOrCreateCached(key);
            DateTime writeTimeUtc = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;

            lock (ModuleCertificateCacheLock)
            {
                if (CachedCertificateContexts.TryGetValue(key, out CachedCertificateContext cached) &&
                    string.Equals(cached.Path, path, StringComparison.Ordinal) &&
                    string.Equals(cached.Password, password, StringComparison.Ordinal) &&
                    cached.WriteTimeUtc == writeTimeUtc)
                {
                    return cached.Context;
                }

                CachedCertificateContexts.Remove(key);
                SslStreamCertificateContext context = SslStreamCertificateContext.Create(
                    certificate,
                    additionalCertificates: null,
                    offline: true);
                CachedCertificateContexts[key] = new CachedCertificateContext(
                    context,
                    path,
                    password,
                    writeTimeUtc);
                return context;
            }
        }
    }

    internal static X509Certificate2 GetCachedRootAuthority()
    {
        lock (RootAuthorityCacheLock)
        {
            string path = GetRootCertificatePath();
            string password = GetCertificatePassword();
            DateTime writeTimeUtc = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;

            if (cachedRootAuthority != null &&
                string.Equals(cachedRootAuthorityPath, path, StringComparison.Ordinal) &&
                string.Equals(cachedRootAuthorityPassword, password, StringComparison.Ordinal) &&
                cachedRootAuthorityWriteTimeUtc == writeTimeUtc &&
                IsRootCertificateValid(cachedRootAuthority))
            {
                return cachedRootAuthority;
            }

            ClearRootAuthorityCacheCore();
            cachedRootAuthority = GetOrCreateRootAuthority();
            cachedRootAuthorityPath = path;
            cachedRootAuthorityPassword = password;
            cachedRootAuthorityWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            return cachedRootAuthority;
        }
    }

    internal static void ClearCertificateCaches()
    {
        lock (ModuleCertificateCacheLock)
        {
            CachedModuleCertificates.Clear();
            CachedCertificateContexts.Clear();
        }

        ClearRootAuthorityCache();
    }

    private static object GetModuleCertificateLock(string moduleName)
    {
        lock (ModuleCertificateCacheLock)
        {
            if (!ModuleCertificateLocks.TryGetValue(moduleName, out object moduleLock))
            {
                moduleLock = new object();
                ModuleCertificateLocks[moduleName] = moduleLock;
            }

            return moduleLock;
        }
    }

    internal static void ClearRootAuthorityCache()
    {
        lock (RootAuthorityCacheLock)
        {
            ClearRootAuthorityCacheCore();
        }
    }

    private static void ClearRootAuthorityCacheCore()
    {
        cachedRootAuthority = null;
        cachedRootAuthorityPath = "";
        cachedRootAuthorityPassword = "";
        cachedRootAuthorityWriteTimeUtc = DateTime.MinValue;
    }

    private static string NormalizeModuleName(string moduleName)
    {
        return string.IsNullOrWhiteSpace(moduleName)
            ? "SocketCommon"
            : moduleName.Trim();
    }

    private sealed class CachedModuleCertificate
    {
        public CachedModuleCertificate(
            X509Certificate2 certificate,
            string path,
            string password,
            DateTime writeTimeUtc)
        {
            this.Certificate = certificate;
            this.Path = path;
            this.Password = password;
            this.WriteTimeUtc = writeTimeUtc;
        }

        public X509Certificate2 Certificate { get; }

        public string Path { get; }

        public string Password { get; }

        public DateTime WriteTimeUtc { get; }
    }

    private sealed class CachedCertificateContext
    {
        public CachedCertificateContext(
            SslStreamCertificateContext context,
            string path,
            string password,
            DateTime writeTimeUtc)
        {
            this.Context = context;
            this.Path = path;
            this.Password = password;
            this.WriteTimeUtc = writeTimeUtc;
        }

        public SslStreamCertificateContext Context { get; }

        public string Path { get; }

        public string Password { get; }

        public DateTime WriteTimeUtc { get; }
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
        if (TryGetClientIdFromModuleName(moduleName, out uint clientId))
        {
            subjectAlternativeNameBuilder.AddDnsName(CreateClientIdDnsName(clientId));
        }

        request.CertificateExtensions.Add(subjectAlternativeNameBuilder.Build());

        using X509Certificate2 certificate = request.Create(
            rootCertificate,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(SecureSocketConnection.ModuleCertificateLifetimeYears),
            CreateSerialNumber());
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

    public static bool HasEnhancedKeyUsage(X509Certificate2 certificate, string oid)
    {
        if (certificate == null || string.IsNullOrWhiteSpace(oid))
        {
            return false;
        }

        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension enhancedKeyUsageExtension)
            {
                foreach (Oid enhancedKeyUsage in enhancedKeyUsageExtension.EnhancedKeyUsages)
                {
                    if (string.Equals(enhancedKeyUsage.Value, oid, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        return false;
    }

    public static bool TryGetClientId(X509Certificate certificate, out uint clientId)
    {
        clientId = 0;
        if (certificate == null)
        {
            return false;
        }

        using X509Certificate2 certificate2 = new(certificate);
        return TryGetClientId(certificate2, out clientId);
    }

    public static bool TryGetClientId(X509Certificate2 certificate, out uint clientId)
    {
        clientId = 0;
        if (certificate == null)
        {
            return false;
        }

        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17")
            {
                continue;
            }

            string formattedSubjectAlternativeNames = extension.Format(false);
            int markerIndex = formattedSubjectAlternativeNames.IndexOf("socket-client-", StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return false;
            }

            int startIndex = markerIndex + "socket-client-".Length;
            int endIndex = startIndex;
            while (endIndex < formattedSubjectAlternativeNames.Length && char.IsDigit(formattedSubjectAlternativeNames[endIndex]))
            {
                endIndex++;
            }

            return endIndex > startIndex &&
                uint.TryParse(
                    formattedSubjectAlternativeNames[startIndex..endIndex],
                    out clientId) &&
                clientId > 0;
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

    private static bool IsModuleCertificateValid(X509Certificate2 certificate, X509Certificate2 rootCertificate, string moduleName)
    {
        if (TryGetClientIdFromModuleName(moduleName, out uint expectedClientId) &&
            (!TryGetClientId(certificate, out uint certificateClientId) || certificateClientId != expectedClientId))
        {
            return false;
        }

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

    private static bool TryGetClientIdFromModuleName(string moduleName, out uint clientId)
    {
        clientId = 0;
        if (string.IsNullOrWhiteSpace(moduleName) ||
            !moduleName.StartsWith("SocketClient-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uint.TryParse(moduleName["SocketClient-".Length..], out clientId) && clientId > 0;
    }

    private static string CreateClientIdDnsName(uint clientId)
    {
        return $"socket-client-{clientId}";
    }

    private static byte[] CreateSerialNumber()
    {
        byte[] serialNumber = new byte[16];
        RandomNumberGenerator.Fill(serialNumber);
        serialNumber[0] &= 0x7F;
        return serialNumber;
    }
}
