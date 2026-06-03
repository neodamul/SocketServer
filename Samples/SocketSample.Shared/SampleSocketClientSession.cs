using SocketClient.Model;
using SocketCommon.Model;

namespace SocketSample.Shared;

public sealed class SampleSocketClientSession : IDisposable
{
    private readonly object syncRoot = new();
    private TcpClient? client;
    private SampleClientSettings settings = new();
    private bool registered;
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
                IsConnected = this.client?.IsConnected() ?? false,
                IsRegistered = this.registered,
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
        TcpClient nextClient = new(current.ClientId, current.ClientName, current.Host, current.Port);
        bool connected = current.UseControlServer
            ? await nextClient.ConnectViaControlServerAsync(current.Host, current.Port)
            : await Task.Run(nextClient.Connect);
        if (!connected)
        {
            nextClient.Dispose();
            this.SetError("Connect failed.");
            return false;
        }

        lock (this.syncRoot)
        {
            this.client?.Dispose();
            this.client = nextClient;
            this.registered = false;
            this.status = "Connected";
            this.lastError = "";
        }

        return true;
    }

    public async Task<bool> RegisterAsync()
    {
        TcpClient activeClient = this.GetRequiredClient();
        bool success = await activeClient.RegisterClientAsync();
        lock (this.syncRoot)
        {
            this.registered = success;
            this.status = success ? "Registered" : "Register failed";
            this.lastError = success ? "" : "Client register failed.";
        }

        return success;
    }

    public async Task<bool> SendMessageAsync(uint targetClientId, string content)
    {
        TcpClient activeClient = this.GetRequiredClient();
        (bool success, ClientMessageAck ack, ClientMessageError error) =
            await activeClient.SendClientMessageAsync(targetClientId, content ?? "");
        lock (this.syncRoot)
        {
            this.status = success ? $"Message delivered to {targetClientId}" : "Message send failed";
            this.lastError = success ? "" : error?.ErrorMessage ?? "Message send failed.";
        }

        return success && ack != null;
    }

    public async Task<ClientMessageDelivery?> ReceiveMessageAsync()
    {
        TcpClient activeClient = this.GetRequiredClient();
        int timeoutSeconds = Math.Max(1, this.Settings.ReceiveTimeoutSeconds);
        Task<(bool Success, ClientMessageDelivery Delivery)> receiveTask =
            activeClient.TryReceiveClientMessageAsync();
        Task completedTask = await Task.WhenAny(
            receiveTask,
            Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (completedTask != receiveTask)
        {
            this.SetError("Receive timed out.");
            return null;
        }

        (bool success, ClientMessageDelivery delivery) = await receiveTask;
        if (!success || delivery == null)
        {
            this.SetError("Message receive failed.");
            return null;
        }

        lock (this.syncRoot)
        {
            this.lastReceivedMessage = $"{delivery.SourceClientId}: {delivery.Content}";
            this.status = "Message received";
            this.lastError = "";
        }

        return delivery;
    }

    public void Disconnect()
    {
        lock (this.syncRoot)
        {
            this.client?.Dispose();
            this.client = null;
            this.registered = false;
            this.status = "Disconnected";
        }
    }

    public void Dispose()
    {
        this.Disconnect();
    }

    private TcpClient GetRequiredClient()
    {
        lock (this.syncRoot)
        {
            if (this.client == null || !this.client.IsConnected())
            {
                throw new InvalidOperationException("Client is not connected.");
            }

            return this.client;
        }
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
