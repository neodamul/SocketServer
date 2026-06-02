using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace SocketCommon.Model;

public sealed class SecureSocketConnection : IDisposable
{
    public const string LocalCertificateSubject = "CN=SocketServerLocal";
    public const string TargetHost = "SocketServerLocal";
    public const string RequireTls13EnvironmentVariable = "SOCKET_REQUIRE_TLS13";
    private const int AuthenticationTimeoutMilliseconds = 5000;

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

    public static async Task<SecureSocketConnection> AuthenticateClientAsync(Socket socket, string moduleName)
    {
        NetworkStream networkStream = new(socket, ownsSocket: true);
        SslStream sslStream = new(
            networkStream,
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) => IsLocalCertificate(certificate));

        await WaitForAuthenticationAsync(sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = TargetHost,
            EnabledSslProtocols = SslProtocols.None,
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
            EnabledSslProtocols = SslProtocols.None,
            ClientCertificateRequired = false,
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

    private static bool IsLocalCertificate(X509Certificate certificate)
    {
        if (certificate == null)
        {
            return false;
        }

        using X509Certificate2 certificate2 = new(certificate);
        return certificate2.Subject.Contains(LocalCertificateSubject, StringComparison.Ordinal);
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
        if (Environment.GetEnvironmentVariable(RequireTls13EnvironmentVariable) == "true" &&
            sslStream.SslProtocol != SslProtocols.Tls13)
        {
            throw new AuthenticationException($"TLS 1.3 is required. negotiated={sslStream.SslProtocol}");
        }
    }
}

public static class LocalCertificateStore
{
    private const string Password = "socket-local-cert";

    public static X509Certificate2 GetOrCreate(string moduleName)
    {
        string path = GetCertificatePath(moduleName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            X509Certificate2 existingCertificate = Load(path);
            if (existingCertificate.GetECDsaPrivateKey() != null && HasSubjectAlternativeName(existingCertificate))
            {
                return existingCertificate;
            }

            existingCertificate.Dispose();
            File.Delete(path);
        }

        if (!File.Exists(path))
        {
            using X509Certificate2 certificate = CreateCertificate();
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, Password));
        }

        return Load(path);
    }

    private static X509Certificate2 Load(string path)
    {
        return X509CertificateLoader.LoadPkcs12FromFile(
            path,
            Password,
            X509KeyStorageFlags.Exportable);
    }

    public static string GetCertificatePath(string moduleName)
    {
        string safeModuleName = string.IsNullOrWhiteSpace(moduleName)
            ? "SocketCommon"
            : moduleName.Trim();
        return Path.Combine(AppContext.BaseDirectory, "Certificates", $"{safeModuleName}.pfx");
    }

    private static X509Certificate2 CreateCertificate()
    {
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new(
            SecureSocketConnection.LocalCertificateSubject,
            ecdsa,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        SubjectAlternativeNameBuilder subjectAlternativeNameBuilder = new();
        subjectAlternativeNameBuilder.AddDnsName(SecureSocketConnection.TargetHost);
        subjectAlternativeNameBuilder.AddDnsName("localhost");
        request.CertificateExtensions.Add(subjectAlternativeNameBuilder.Build());

        X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));
        return certificate;
    }

    private static bool HasSubjectAlternativeName(X509Certificate2 certificate)
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
}
