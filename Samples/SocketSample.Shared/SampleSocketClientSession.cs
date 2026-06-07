using SocketClient.Model;
using SocketCommon.Model;

namespace SocketSample.Shared;

public sealed class SampleSocketClientSession : IDisposable
{
    private readonly object syncRoot = new();
    private SocketClientSession? session;
    private SampleClientSettings settings = new();
    private string status = "Disconnected";
    private string lastReceivedMessage = "";
    private string lastError = "";

    public SampleClientSettings Settings
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.settings.Clone();
            }
        }
    }

    public SampleClientState GetState()
    {
        lock (this.syncRoot)
        {
            return new SampleClientState
            {
                IsConnected = this.session?.IsConnected ?? false,
                IsRegistered = this.session?.IsRegistered ?? false,
                ClientId = this.settings.ClientId,
                Host = this.settings.Host,
                Port = this.settings.Port,
                UseControlServer = this.settings.UseControlServer,
                Status = this.status,
                LastReceivedMessage = this.lastReceivedMessage,
                LastError = this.lastError
            };
        }
    }

    public void Configure(SampleClientSettings nextSettings)
    {
        if (nextSettings == null)
        {
            throw new ArgumentNullException(nameof(nextSettings));
        }

        lock (this.syncRoot)
        {
            this.settings = nextSettings.Clone();
            this.status = "Configured";
            this.lastError = "";
        }

        SecureSocketConnection.Configure(nextSettings.Security);
        SocketFactory.Configure(nextSettings.SocketOptions);
    }

    public async Task<bool> ConnectAsync()
    {
        SampleClientSettings current = this.Settings;
        SocketClientSession nextSession = new();
        this.AttachSessionEvents(nextSession);

        bool success = await nextSession.ConnectAndRegisterAsync(
            current.ClientId,
            current.ClientName,
            current.Host,
            current.Port,
            current.UseControlServer,
            current.HealthCheckIntervalSeconds,
            current.ReconnectRetrySeconds,
            current.DuplicateRejectBackoffSeconds);

        lock (this.syncRoot)
        {
            this.session?.Dispose();
            this.session = nextSession;
            this.status = success ? "Connected and registered" : "Reconnecting";
            this.lastError = success ? "" : "Connect/register failed. Retrying.";
        }

        return success;
    }

    public async Task<bool> RegisterAsync()
    {
        lock (this.syncRoot)
        {
            this.status = this.session?.IsRegistered == true
                ? "Already registered"
                : "Client is not connected.";
            this.lastError = this.session?.IsRegistered == true ? "" : "Client is not connected.";
        }

        await Task.CompletedTask;
        return this.session?.IsRegistered == true;
    }

    public async Task<bool> SendMessageAsync(uint targetClientId, string content, int ttlSeconds = 10)
    {
        SocketClientSession activeSession = this.GetRequiredSession();
        bool success = await activeSession.SendMessageAsync(targetClientId, content, ttlSeconds);
        lock (this.syncRoot)
        {
            this.status = success ? $"Message sent to {targetClientId}" : "Message send failed";
            this.lastError = success ? "" : "Message send failed.";
        }

        return success;
    }

    public async Task<ClientMessageDelivery?> ReceiveMessageAsync()
    {
        lock (this.syncRoot)
        {
            this.status = this.session?.IsRegistered == true
                ? "Receive loop is running"
                : "Receive loop is not running";
            this.lastError = "";
        }

        await Task.CompletedTask;
        return null;
    }

    public void Disconnect()
    {
        lock (this.syncRoot)
        {
            this.session?.Dispose();
            this.session = null;
            this.status = "Disconnected";
        }
    }

    public void Dispose()
    {
        this.Disconnect();
    }

    private SocketClientSession GetRequiredSession()
    {
        lock (this.syncRoot)
        {
            if (this.session == null || !this.session.IsRegistered)
            {
                throw new InvalidOperationException("Client is not connected and registered.");
            }

            return this.session;
        }
    }

    private void AttachSessionEvents(SocketClientSession targetSession)
    {
        targetSession.MessageReceived += delivery =>
        {
            lock (this.syncRoot)
            {
                this.lastReceivedMessage = $"{delivery.SourceClientId}: {delivery.Content}";
                this.status = "Message received";
                this.lastError = "";
            }
        };
        targetSession.MessageAcknowledged += ack =>
        {
            lock (this.syncRoot)
            {
                this.status = ack.Delivered
                    ? $"Message delivered to {ack.TargetClientId}"
                    : $"Message not delivered to {ack.TargetClientId}";
                this.lastError = ack.Delivered ? "" : "Message was not delivered.";
            }
        };
        targetSession.MessageFailed += error =>
        {
            this.SetError(error.ErrorMessage);
        };
        targetSession.HealthCheckReceived += healthCheck =>
        {
            if (healthCheck.Type != HealthCheckMessageType.Pong)
            {
                return;
            }

            lock (this.syncRoot)
            {
                this.status = "Healthcheck pong received";
                this.lastError = "";
            }
        };
    }

    private void SetError(string error)
    {
        lock (this.syncRoot)
        {
            this.status = error;
            this.lastError = error;
        }
    }
}
