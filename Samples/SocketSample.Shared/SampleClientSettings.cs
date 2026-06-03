using SocketCommon.Configuration;

namespace SocketSample.Shared;

public sealed class SampleClientSettings
{
    public int ClientId { get; set; } = 1;

    public string ClientName { get; set; } = "sample-client-1";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5000;

    public bool UseControlServer { get; set; } = true;

    public int ReceiveTimeoutSeconds { get; set; } = 10;

    public SocketSecurityConfig Security { get; set; } = new();

    public SampleClientSettings Clone()
    {
        return new SampleClientSettings
        {
            ClientId = this.ClientId,
            ClientName = this.ClientName,
            Host = this.Host,
            Port = this.Port,
            UseControlServer = this.UseControlServer,
            ReceiveTimeoutSeconds = this.ReceiveTimeoutSeconds,
            Security = new SocketSecurityConfig
            {
                TransportMode = this.Security.TransportMode,
                TlsProtocol = this.Security.TlsProtocol,
                RequireTls13 = this.Security.RequireTls13,
                RequireClientCertificate = this.Security.RequireClientCertificate,
                CertificateDirectory = this.Security.CertificateDirectory,
                CertificatePasswordEnvironmentVariable = this.Security.CertificatePasswordEnvironmentVariable,
                CertificateRenewBeforeDays = this.Security.CertificateRenewBeforeDays,
                RootCertificateLifetimeYears = this.Security.RootCertificateLifetimeYears,
                ModuleCertificateLifetimeYears = this.Security.ModuleCertificateLifetimeYears,
                AuthenticationTimeoutMilliseconds = this.Security.AuthenticationTimeoutMilliseconds,
                MessageEncryptionSecretEnvironmentVariable = this.Security.MessageEncryptionSecretEnvironmentVariable
            }
        };
    }
}
