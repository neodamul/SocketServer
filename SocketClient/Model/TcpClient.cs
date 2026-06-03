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
            this.SetIpAddress(IPAddress.Parse(ipAddress));
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
            this.Connection = SecureSocketConnection.AuthenticateClientAsync(this.Socket, "SocketClient").GetAwaiter().GetResult();
            Logger.Info($"Client connected. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}");
            return true;
        }
        catch (SocketException exception)
        {
            Logger.Warn($"Client connect failed. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}", exception);
            return false;
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn($"Client connect failed because socket is disposed. clientId={this.ClientId}", exception);
            return false;
        }
        catch (AuthenticationException exception)
        {
            Logger.Warn($"Client TLS handshake failed. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}", exception);
            return false;
        }
        catch (IOException exception)
        {
            Logger.Warn($"Client TLS connection failed. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}", exception);
            return false;
        }
        catch (TimeoutException exception)
        {
            Logger.Warn($"Client connect timed out. clientId={this.ClientId}, endpoint={this.IpAddress}:{this.Port}", exception);
            return false;
        }
    }

    public async Task<bool> ConnectViaControlServerAsync(string controlHost, int controlPort)
    {
        return await this.ConnectViaControlServersAsync(new[] { new IPEndPoint(IPAddress.Parse(controlHost), controlPort) });
    }

    public async Task<bool> ConnectViaControlServersAsync(IEnumerable<IPEndPoint> controlEndpoints)
    {
        foreach (IPEndPoint endpoint in controlEndpoints)
        {
            try
            {
                Socket controlSocket = SocketFactory.CreateTcpSocket(this.Family);
                await SocketFactory.ConnectAsync(controlSocket, endpoint.Address, endpoint.Port);
                using SecureSocketConnection controlConnection =
                    await SecureSocketConnection.AuthenticateClientAsync(controlSocket, "SocketClient");
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
                    continue;
                }

                this.SetIpAddress(response.Host);
                this.SetPort(response.Port);
                return this.Connect();
            }
            catch (SocketException exception)
            {
                Logger.Warn($"ControlServer route request failed. clientId={this.ClientId}, endpoint={endpoint}", exception);
            }
            catch (TimeoutException exception)
            {
                Logger.Warn($"ControlServer route request timed out. clientId={this.ClientId}, endpoint={endpoint}", exception);
            }
        }

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
        this.healthCheckTask = this.RunHealthCheckLoopAsync(interval, this.healthCheckCancellation.Token);
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
        if (this.Socket == null)
        {
            Logger.Warn($"Client register skipped because socket is not initialized. clientId={this.ClientId}");
            return false;
        }

        if (!await ClientMessageProtocol.SendRegisterAsync(this.Connection, this.ClientId))
        {
            return false;
        }

        (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(this.Connection);
        return success &&
            ClientMessageProtocol.TryDecodeRegisterAck(frame, out ClientRegisterAck ack) &&
            ack.Success;
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
        if (!await ClientMessageProtocol.SendClientMessageAsync(this.Connection, request))
        {
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
            return (ack.Delivered, ack, null);
        }

        if (ClientMessageProtocol.TryDecodeError(frame, out ClientMessageError error))
        {
            return (false, null, error);
        }

        return (false, null, new ClientMessageError
        {
            MessageToken = request.MessageToken,
            SourceClientId = request.SourceClientId,
            TargetClientId = request.TargetClientId,
            ErrorCode = "InvalidResponse",
            ErrorMessage = "Client message response was invalid."
        });
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
            return (false, null);
        }

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
                Task completedTask = await Task.WhenAny(receiveTask, Task.Delay(interval, cancellationToken));
                if (completedTask != receiveTask)
                {
                    break;
                }

                (bool success, HealthCheckMessage message) = await receiveTask;
                if (!success || message.Type != HealthCheckMessageType.Pong)
                {
                    break;
                }
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
