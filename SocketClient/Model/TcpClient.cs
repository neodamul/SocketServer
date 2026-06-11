using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon;
using SocketCommon.Interface;
using SocketCommon.Logging;
using SocketCommon.Model;

namespace SocketClient.Model;
public class TcpClient : IClient, IDisposable
{
    private static readonly SocketLogger Logger = SocketLogManager.GetLogger<TcpClient>();
    private static readonly TimeSpan MinimumHealthCheckResponseTimeout = TimeSpan.FromMilliseconds(250);
    private const int MaxHealthCheckMissCount = 3;

    protected Socket Socket = null;
    protected SecureSocketConnection Connection = null;

    private bool disposedValue;
    private CancellationTokenSource healthCheckCancellation;
    private Task healthCheckTask;

    private int Id { get; set; }
    private string Name { get; set; }
    private AddressFamily Family { get; set; }
    protected IPAddress IpAddress { get; private set; }
    protected int Port { get; private set; }
    protected uint ClientId => this.Id < 0 ? 0 : (uint)this.Id;

    public TcpClient() : this(0, null)
    { }

    public TcpClient(int id, string name)
        : this(id, name, null, Constants.LocalHostPort)
    { }

    public TcpClient(int id, string name, string ipAddress, int port)
    {
        this.Id = id;
        this.Name = name;
        this.Family = AddressFamily.InterNetwork;
        this.SetIpAddress(ipAddress);
        this.SetPort(port);
    }

    public void Initialize()
    {
        LocalCertificateStore.GetOrCreate("SocketClient");
        this.Connection?.Dispose();
        this.Socket?.Dispose();
        this.Socket = SocketFactory.CreateTcpSocket(this.Family);
        Logger.Info($"Client socket initialized. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}");
    }

    public void SetIpAddress(IPAddress ipAddress)
    {
        this.IpAddress = ipAddress;
    }

    public void SetIpAddress(string ipAddress)
    {
        if (String.IsNullOrEmpty(ipAddress))
        {
            this.SetIpAddress(IPAddress.Parse(Constants.LocalHostIpAddres));
        }
        else
        {
            this.SetIpAddress(SocketFactory.ResolveAddress(ipAddress, this.Family));
        }
    }

    public string GetIpAddress()
    {
        return this.IpAddress.ToString();
    }

    public void SetPort(int port)
    {
        this.Port = port;
    }

    public int GetPort()
    {
        return Port;
    }

    public bool Connect()
    {
        try
        {
            if (this.Socket == null)
            {
                this.Initialize();
            }

            SocketFactory.ConnectAsync(this.Socket, this.IpAddress, this.Port).GetAwaiter().GetResult();
            this.Connection = SecureSocketConnection.AuthenticateClientAsync(this.Socket, this.GetCertificateModuleName()).GetAwaiter().GetResult();
            Logger.Info($"Client connected. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}");
            return true;
        }
        catch (SocketException exception)
        {
            Logger.Warn($"Client connect failed. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}", exception);
            return this.ResetAfterConnectFailure();
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn($"Client connect failed because socket is disposed. clientId={this.ClientId}", exception);
            return this.ResetAfterConnectFailure();
        }
        catch (AuthenticationException exception)
        {
            Logger.Warn($"Client TLS handshake failed. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}", exception);
            return this.ResetAfterConnectFailure();
        }
        catch (IOException exception)
        {
            Logger.Warn($"Client TLS connection failed. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}", exception);
            return this.ResetAfterConnectFailure();
        }
        catch (TimeoutException exception)
        {
            Logger.Warn($"Client connect timed out. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}", exception);
            return this.ResetAfterConnectFailure();
        }
    }

    public async Task<bool> ConnectViaControlServerAsync(string controlHost, int controlPort)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (await this.ConnectViaControlEndpointAsync(controlHost, controlPort))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> ConnectViaControlServersAsync(IEnumerable<IPEndPoint> controlEndpoints, int maxRouteAttempts = 2)
    {
        List<IPEndPoint> endpoints = new();
        if (controlEndpoints != null)
        {
            foreach (IPEndPoint endpoint in controlEndpoints)
            {
                if (endpoint != null)
                {
                    endpoints.Add(endpoint);
                }
            }
        }

        if (endpoints.Count == 0)
        {
            return false;
        }

        int attempts = Math.Max(1, maxRouteAttempts);
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            foreach (IPEndPoint endpoint in endpoints)
            {
                if (await this.ConnectViaControlEndpointAsync(endpoint.Address.ToString(), endpoint.Port))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<bool> ConnectViaControlEndpointAsync(string controlHost, int controlPort)
    {
        string endpoint = $"{controlHost}:{controlPort}";
        try
        {
            string serverHost;
            int serverPort;
            Logger.Info($"ControlServer route request started. clientId={this.ClientId}, endpoint={endpoint}");
            using (Socket controlSocket = SocketFactory.CreateTcpSocket(this.Family))
            {
                await SocketFactory.ConnectAsync(controlSocket, controlHost, controlPort);
                using SecureSocketConnection controlConnection =
                    await SecureSocketConnection.AuthenticateClientAsync(controlSocket, this.GetCertificateModuleName());
                (bool success, SocketMessageFrame frame) = await ControlProtocol.SendAndReceiveAsync(
                    controlConnection,
                    this.ClientId,
                    ControlMessageIds.RouteRequest,
                    new RouteRequest
                    {
                        ClientId = this.ClientId,
                        RoutingPolicy = "MostAvailableConnections"
                    });

                if (!success ||
                    !ControlProtocol.TryDecode(frame, ControlMessageIds.RouteResponse, out RouteResponse response) ||
                    !response.Success)
                {
                    Logger.Warn($"ControlServer route request did not return usable server. clientId={this.ClientId}, endpoint={endpoint}, success={success}");
                    return false;
                }

                Logger.Info($"ControlServer route request completed. clientId={this.ClientId}, endpoint={endpoint}, serverInstanceId={response.InstanceId}, serverEndpoint={response.Host}:{response.Port}, reservationId={response.ReservationId}");
                serverHost = response.Host;
                serverPort = response.Port;
            }

            this.SetIpAddress(serverHost);
            this.SetPort(serverPort);
            return this.Connect();
        }
        catch (SocketException exception)
        {
            Logger.Warn($"ControlServer route request failed. clientId={this.ClientId}, endpoint={endpoint}", exception);
        }
        catch (AuthenticationException exception)
        {
            Logger.Warn($"ControlServer route request TLS handshake failed. clientId={this.ClientId}, endpoint={endpoint}", exception);
        }
        catch (IOException exception)
        {
            Logger.Warn($"ControlServer route request stream failed. clientId={this.ClientId}, endpoint={endpoint}", exception);
        }
        catch (TimeoutException exception)
        {
            Logger.Warn($"ControlServer route request timed out. clientId={this.ClientId}, endpoint={endpoint}", exception);
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn($"ControlServer route request socket was disposed. clientId={this.ClientId}, endpoint={endpoint}", exception);
        }

        return false;
    }

    private string GetCertificateModuleName()
    {
        return this.ClientId > 0 ? $"SocketClient-{this.ClientId}" : "SocketClient";
    }

    private bool ResetAfterConnectFailure()
    {
        this.Connection?.Dispose();
        this.Connection = null;
        this.Socket?.Dispose();
        this.Socket = null;
        return false;
    }

    public bool Disconnect()
    {
        this.StopHealthCheckLoop();
        return this.CloseConnection();
    }

    public Task<bool> DisconnectAsync()
    {
        return this.DisconnectAsync(TimeSpan.FromSeconds(5));
    }

    public async Task<bool> DisconnectAsync(TimeSpan timeout)
    {
        await this.StopHealthCheckLoopAsync(timeout);
        return this.CloseConnection();
    }

    private bool CloseConnection()
    {
        if (this.Connection == null && this.Socket == null)
        {
            return true;
        }

        try
        {
            if (this.Connection != null)
            {
                this.Connection.Dispose();
            }
            else if (this.Socket.Connected)
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
            this.Connection = null;
            this.Socket?.Dispose();
            this.Socket = null;
            Logger.Info($"Client disconnected. clientId={this.ClientId}");
        }

        return true;
    }

    public bool StartHealthCheckLoop()
    {
        return this.StartHealthCheckLoop(TimeSpan.FromSeconds(HealthCheckProtocol.KeepAliveIntervalSeconds));
    }

    public bool StartHealthCheckLoop(TimeSpan interval)
    {
        if (this.Connection == null || interval <= TimeSpan.Zero)
        {
            return false;
        }

        if (this.healthCheckTask != null && !this.healthCheckTask.IsCompleted)
        {
            return true;
        }

        this.healthCheckCancellation?.Dispose();
        this.healthCheckCancellation = new CancellationTokenSource();
        this.healthCheckTask = DedicatedWorker.Start(
            token => this.RunHealthCheckLoopAsync(interval, token),
            this.healthCheckCancellation.Token);
        Logger.Info($"Healthcheck loop started. clientId={this.ClientId}, intervalSeconds={interval.TotalSeconds}");
        return true;
    }

    public void StopHealthCheckLoop()
    {
        this.healthCheckCancellation?.Cancel();
        this.healthCheckCancellation?.Dispose();
        this.healthCheckCancellation = null;
        this.healthCheckTask = null;
    }

    public Task StopHealthCheckLoopAsync()
    {
        return this.StopHealthCheckLoopAsync(TimeSpan.FromSeconds(5));
    }

    public async Task StopHealthCheckLoopAsync(TimeSpan timeout)
    {
        CancellationTokenSource cancellation = this.healthCheckCancellation;
        Task task = this.healthCheckTask;
        try
        {
            cancellation?.Cancel();
            if (task != null && !task.IsCompleted)
            {
                Task completed = await Task.WhenAny(task, Task.Delay(timeout));
                if (completed == task)
                {
                    await task;
                }
            }
        }
        catch (TaskCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation?.Dispose();
            if (this.healthCheckCancellation == cancellation)
            {
                this.healthCheckCancellation = null;
                this.healthCheckTask = null;
            }
        }
    }

    public bool IsConnected()
    {
        return this.Connection?.IsConnected ?? false;
    }

    public bool SendHealthCheck()
    {
        return SendHealthCheckAsync().GetAwaiter().GetResult();
    }

    public async Task<bool> SendHealthCheckAsync()
    {
        if (this.Connection == null)
        {
            Logger.Warn($"Healthcheck send skipped because socket is not initialized. clientId={this.ClientId}");
            return false;
        }

        bool sent = await HealthCheckProtocol.SendAsync(this.Connection, HealthCheckProtocol.CreatePing(this.ClientId));
        Logger.Debug($"Healthcheck ping sent. clientId={this.ClientId}, success={sent}");
        return sent;
    }

    public bool SendHealthCheckResponse()
    {
        return SendHealthCheckResponseAsync().GetAwaiter().GetResult();
    }

    public async Task<bool> SendHealthCheckResponseAsync()
    {
        if (this.Connection == null)
        {
            Logger.Warn($"Healthcheck response send skipped because socket is not initialized. clientId={this.ClientId}");
            return false;
        }

        bool sent = await HealthCheckProtocol.SendAsync(this.Connection, HealthCheckProtocol.CreatePong(this.ClientId));
        Logger.Debug($"Healthcheck pong sent. clientId={this.ClientId}, success={sent}");
        return sent;
    }

    public bool TryReceiveHealthCheck(out HealthCheckMessage message)
    {
        (bool success, HealthCheckMessage receivedMessage) = TryReceiveHealthCheckAsync().GetAwaiter().GetResult();
        message = receivedMessage;
        return success;
    }

    public async Task<(bool Success, HealthCheckMessage Message)> TryReceiveHealthCheckAsync()
    {
        if (this.Connection == null)
        {
            return (false, null);
        }

        return await HealthCheckProtocol.TryReceiveAsync(this.Connection);
    }

    public bool SendHelloWorldRequest()
    {
        return SendHelloWorldRequestAsync().GetAwaiter().GetResult();
    }

    public async Task<bool> SendHelloWorldRequestAsync()
    {
        if (this.Connection == null)
        {
            Logger.Warn($"HelloWorld request send skipped because socket is not initialized. clientId={this.ClientId}");
            return false;
        }

        bool sent = await HelloWorldProtocol.SendAsync(this.Connection, HelloWorldProtocol.CreateRequest(this.ClientId));
        Logger.Debug($"HelloWorld request sent. clientId={this.ClientId}, success={sent}");
        return sent;
    }

    public async Task<bool> RegisterClientAsync()
    {
        (bool received, ClientRegisterAck ack) = await this.RegisterClientWithAckAsync();
        return received && ack.Success;
    }

    public async Task<(bool Success, ClientRegisterAck Ack)> RegisterClientWithAckAsync()
    {
        if (this.Socket == null)
        {
            Logger.Warn($"Client register skipped because socket is not initialized. clientId={this.ClientId}");
            return (false, null);
        }

        if (!await ClientMessageProtocol.SendRegisterAsync(this.Connection, this.ClientId))
        {
            Logger.Warn($"Client register send failed. clientId={this.ClientId}");
            return (false, null);
        }

        Logger.Info($"Client register request sent. clientId={this.ClientId}");
        (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(this.Connection);
        ClientRegisterAck ack = null;
        bool decoded = success && ClientMessageProtocol.TryDecodeRegisterAck(frame, out ack);
        Logger.Info($"Client register response received. clientId={this.ClientId}, success={decoded && ack.Success}");
        return decoded ? (true, ack) : (false, null);
    }

    public async Task<(bool Success, ClientMessageAck Ack, ClientMessageError Error)> SendClientMessageAsync(
        uint targetClientId,
        string content,
        int ttlSeconds = 10)
    {
        if (this.Connection == null)
        {
            Logger.Warn($"Client message send skipped because socket is not initialized. clientId={this.ClientId}");
            return (false, null, new ClientMessageError
            {
                SourceClientId = this.ClientId,
                TargetClientId = targetClientId,
                ErrorCode = "SocketNotInitialized",
                ErrorMessage = "Client socket is not initialized."
            });
        }

        ClientMessageSendRequest request = ClientMessageProtocol.CreateSendRequest(
            this.ClientId,
            targetClientId,
            content,
            ttlSeconds: ttlSeconds);
        Logger.Info($"Client message send started. clientId={this.ClientId}, targetClientId={targetClientId}, messageToken={request.MessageToken}, ttlSeconds={ttlSeconds}");
        if (!await ClientMessageProtocol.SendClientMessageAsync(this.Connection, request))
        {
            Logger.Warn($"Client message send failed before response. clientId={this.ClientId}, targetClientId={targetClientId}, messageToken={request.MessageToken}");
            return (false, null, new ClientMessageError
            {
                MessageToken = request.MessageToken,
                SourceClientId = request.SourceClientId,
                TargetClientId = request.TargetClientId,
                ErrorCode = "SendFailed",
                ErrorMessage = "Client message send failed."
            });
        }

        (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(this.Connection);
        if (!success)
        {
            Logger.Warn($"Client message response not received. clientId={this.ClientId}, targetClientId={targetClientId}, messageToken={request.MessageToken}");
            return (false, null, new ClientMessageError
            {
                MessageToken = request.MessageToken,
                SourceClientId = request.SourceClientId,
                TargetClientId = request.TargetClientId,
                ErrorCode = "ResponseNotReceived",
                ErrorMessage = "Client message response was not received."
            });
        }

        if (ClientMessageProtocol.TryDecodeAck(frame, out ClientMessageAck ack))
        {
            Logger.Info($"Client message ack received. clientId={this.ClientId}, targetClientId={targetClientId}, messageToken={request.MessageToken}, delivered={ack.Delivered}, targetInstanceId={ack.TargetInstanceId}");
            return (ack.Delivered, ack, null);
        }

        if (ClientMessageProtocol.TryDecodeError(frame, out ClientMessageError error))
        {
            Logger.Warn($"Client message error received. clientId={this.ClientId}, targetClientId={targetClientId}, messageToken={request.MessageToken}, errorCode={error.ErrorCode}, error={error.ErrorMessage}");
            return (false, null, error);
        }

        Logger.Warn($"Client message invalid response received. clientId={this.ClientId}, targetClientId={targetClientId}, messageToken={request.MessageToken}, messageId={frame.MessageId}");
        return (false, null, new ClientMessageError
        {
            MessageToken = request.MessageToken,
            SourceClientId = request.SourceClientId,
            TargetClientId = request.TargetClientId,
            ErrorCode = "InvalidResponse",
            ErrorMessage = "Client message response was invalid."
        });
    }

    public async Task<(bool Success, ClientMessageSendRequest Request, ClientMessageError Error)> SendClientMessageRequestAsync(
        uint targetClientId,
        string content,
        int ttlSeconds = 10)
    {
        if (this.Connection == null)
        {
            Logger.Warn($"Client message send skipped because socket is not initialized. clientId={this.ClientId}");
            return (false, null, new ClientMessageError
            {
                SourceClientId = this.ClientId,
                TargetClientId = targetClientId,
                ErrorCode = "SocketNotInitialized",
                ErrorMessage = "Client socket is not initialized."
            });
        }

        ClientMessageSendRequest request = ClientMessageProtocol.CreateSendRequest(
            this.ClientId,
            targetClientId,
            content,
            ttlSeconds: ttlSeconds);
        Logger.Info($"Client message request sent. clientId={this.ClientId}, targetClientId={targetClientId}, messageToken={request.MessageToken}, ttlSeconds={ttlSeconds}");
        if (!await ClientMessageProtocol.SendClientMessageAsync(this.Connection, request))
        {
            Logger.Warn($"Client message request send failed. clientId={this.ClientId}, targetClientId={targetClientId}, messageToken={request.MessageToken}");
            return (false, request, new ClientMessageError
            {
                MessageToken = request.MessageToken,
                SourceClientId = request.SourceClientId,
                TargetClientId = request.TargetClientId,
                ErrorCode = "SendFailed",
                ErrorMessage = "Client message send failed."
            });
        }

        return (true, request, null);
    }

    public async Task<(bool Success, SocketMessageFrame Frame)> TryReceiveFrameAsync()
    {
        if (this.Connection == null)
        {
            return (false, null);
        }

        return await SocketMessageFrame.TryReceiveAsync(this.Connection);
    }

    public async Task<(bool Success, SocketMessageFrame Frame)> TryReceiveFrameAsync(
        int headerTimeoutMilliseconds,
        int payloadTimeoutMilliseconds)
    {
        if (this.Connection == null)
        {
            return (false, null);
        }

        return await SocketMessageFrame.TryReceiveAsync(
            this.Connection,
            headerTimeoutMilliseconds,
            payloadTimeoutMilliseconds);
    }

    public async Task<(bool Success, ClientMessageDelivery Delivery)> TryReceiveClientMessageAsync()
    {
        if (this.Connection == null)
        {
            return (false, null);
        }

        (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(this.Connection);
        if (!success || !ClientMessageProtocol.TryDecodeDelivery(frame, out ClientMessageDelivery delivery))
        {
            Logger.Debug($"Client message delivery receive skipped. clientId={this.ClientId}, success={success}");
            return (false, null);
        }

        Logger.Info($"Client message delivery received. clientId={this.ClientId}, sourceClientId={delivery.SourceClientId}, targetClientId={delivery.TargetClientId}, messageToken={delivery.MessageToken}");
        return (true, delivery);
    }

    public bool TryReceiveHelloWorldResponse(out HelloWorldResponse response)
    {
        (bool success, HelloWorldResponse receivedResponse) = TryReceiveHelloWorldResponseAsync().GetAwaiter().GetResult();
        response = receivedResponse;
        return success;
    }

    public async Task<(bool Success, HelloWorldResponse Response)> TryReceiveHelloWorldResponseAsync()
    {
        if (this.Connection == null)
        {
            return (false, null);
        }

        return await HelloWorldProtocol.TryReceiveResponseAsync(this.Connection);
    }

    private async Task RunHealthCheckLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        int missedResponses = 0;
        TimeSpan responseTimeout = NormalizeHealthCheckResponseTimeout(interval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken);
                if (!await this.SendHealthCheckAsync())
                {
                    break;
                }

                Task<(bool Success, HealthCheckMessage Message)> receiveTask = this.TryReceiveHealthCheckAsync();
                Task completedTask = await Task.WhenAny(receiveTask, Task.Delay(responseTimeout, cancellationToken));
                if (completedTask != receiveTask)
                {
                    missedResponses++;
                    Logger.Warn($"Healthcheck pong timed out. clientId={this.ClientId}, missedResponses={missedResponses}, timeoutMs={responseTimeout.TotalMilliseconds}");
                    if (missedResponses >= MaxHealthCheckMissCount)
                    {
                        break;
                    }

                    continue;
                }

                (bool success, HealthCheckMessage message) = await receiveTask;
                if (!success || message.Type != HealthCheckMessageType.Pong)
                {
                    missedResponses++;
                    Logger.Warn($"Healthcheck pong invalid. clientId={this.ClientId}, missedResponses={missedResponses}, success={success}, messageType={message?.Type}");
                    if (missedResponses >= MaxHealthCheckMissCount)
                    {
                        break;
                    }

                    continue;
                }

                missedResponses = 0;
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            Logger.Warn($"Healthcheck loop failed. clientId={this.ClientId}");
            this.Disconnect();
        }
    }

    private static TimeSpan NormalizeHealthCheckResponseTimeout(TimeSpan interval)
    {
        return interval < MinimumHealthCheckResponseTimeout
            ? MinimumHealthCheckResponseTimeout
            : interval;
    }

    public override string ToString()
    {
        return Id + ":" + Name + ":" + Family + ":" + IpAddress + ":" + Port;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                this.Disconnect();
            }

            disposedValue = true;
        }
    }

    // // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
    // ~TcpClient()
    // {
    //     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
    //     Dispose(disposing: false);
    // }

    public void Dispose()
    {
        // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
