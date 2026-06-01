using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SocketCommon;
using SocketCommon.Interface;
using SocketCommon.Model;

namespace SocketClient.Model;
public class TcpClient : IClient, IDisposable
{
    protected Socket Socket = null;

    private bool disposedValue;

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
        this.Socket?.Dispose();
        this.Socket = SocketFactory.CreateTcpSocket(this.Family);
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

            this.Socket.Connect(new IPEndPoint(this.IpAddress, this.Port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool Disconnect()
    {
        if (this.Socket == null)
        {
            return true;
        }

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
            this.Socket.Dispose();
            this.Socket = null;
        }

        return true;
    }

    public bool IsConnected()
    {
        return this.Socket?.Connected ?? false;
    }

    public bool SendHealthCheck()
    {
        return SendHealthCheckAsync().GetAwaiter().GetResult();
    }

    public Task<bool> SendHealthCheckAsync()
    {
        return this.Socket == null
            ? Task.FromResult(false)
            : HealthCheckProtocol.SendAsync(this.Socket, HealthCheckProtocol.CreatePing(this.ClientId));
    }

    public bool SendHealthCheckResponse()
    {
        return SendHealthCheckResponseAsync().GetAwaiter().GetResult();
    }

    public Task<bool> SendHealthCheckResponseAsync()
    {
        return this.Socket == null
            ? Task.FromResult(false)
            : HealthCheckProtocol.SendAsync(this.Socket, HealthCheckProtocol.CreatePong(this.ClientId));
    }

    public bool TryReceiveHealthCheck(out HealthCheckMessage message)
    {
        (bool success, HealthCheckMessage receivedMessage) = TryReceiveHealthCheckAsync().GetAwaiter().GetResult();
        message = receivedMessage;
        return success;
    }

    public async Task<(bool Success, HealthCheckMessage Message)> TryReceiveHealthCheckAsync()
    {
        if (this.Socket == null)
        {
            return (false, null);
        }

        return await HealthCheckProtocol.TryReceiveAsync(this.Socket);
    }

    public bool SendHelloWorldRequest()
    {
        return SendHelloWorldRequestAsync().GetAwaiter().GetResult();
    }

    public Task<bool> SendHelloWorldRequestAsync()
    {
        return this.Socket == null
            ? Task.FromResult(false)
            : HelloWorldProtocol.SendAsync(this.Socket, HelloWorldProtocol.CreateRequest(this.ClientId));
    }

    public bool TryReceiveHelloWorldResponse(out HelloWorldResponse response)
    {
        (bool success, HelloWorldResponse receivedResponse) = TryReceiveHelloWorldResponseAsync().GetAwaiter().GetResult();
        response = receivedResponse;
        return success;
    }

    public async Task<(bool Success, HelloWorldResponse Response)> TryReceiveHelloWorldResponseAsync()
    {
        if (this.Socket == null)
        {
            return (false, null);
        }

        return await HelloWorldProtocol.TryReceiveResponseAsync(this.Socket);
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
