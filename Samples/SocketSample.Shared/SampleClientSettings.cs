using SocketCommon.Configuration;

namespace SocketSample.Shared;

public sealed class SampleClientSettings
{
    public int ClientId { get; set; } = 1;

    public string ClientName { get; set; } = "sample-client-1";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 10000;

    public bool UseControlServer { get; set; } = true;

    public int ReceiveTimeoutSeconds { get; set; } = 10;

    public int HealthCheckIntervalSeconds { get; set; } = 30;

    public int ReconnectRetrySeconds { get; set; } = 30;

    public int DuplicateRejectBackoffSeconds { get; set; } = 90;

    public SocketSecurityConfig Security { get; set; } = new();

    public SocketOperationConfig SocketOptions { get; set; } = new();

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
            HealthCheckIntervalSeconds = this.HealthCheckIntervalSeconds,
            ReconnectRetrySeconds = this.ReconnectRetrySeconds,
            DuplicateRejectBackoffSeconds = this.DuplicateRejectBackoffSeconds,
            Security = new SocketSecurityConfig
            {
                Profile = this.Security.Profile,
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
                MessageEncryptionSecretEnvironmentVariable = this.Security.MessageEncryptionSecretEnvironmentVariable,
                TrustedNetwork = this.Security.TrustedNetwork
            },
            SocketOptions = new SocketOperationConfig
            {
                ConnectTimeoutSeconds = this.SocketOptions.ConnectTimeoutSeconds,
                ReadTimeoutSeconds = this.SocketOptions.ReadTimeoutSeconds,
                WriteTimeoutSeconds = this.SocketOptions.WriteTimeoutSeconds
            }
        };
    }
}
