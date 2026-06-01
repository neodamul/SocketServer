using System;
using System.Net;
using System.Net.Sockets;
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
        this.Socket = new Socket(this.Family, SocketType.Stream, ProtocolType.Tcp);
        this.Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
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
        if (this.Socket == null)
        {
            return false;
        }

        return HealthCheckProtocol.Send(this.Socket, HealthCheckProtocol.CreatePing());
    }

    public bool SendHealthCheckResponse()
    {
        if (this.Socket == null)
        {
            return false;
        }

        return HealthCheckProtocol.Send(this.Socket, HealthCheckProtocol.CreatePong());
    }

    public bool TryReceiveHealthCheck(out HealthCheckMessage message)
    {
        message = null;
        if (this.Socket == null)
        {
            return false;
        }

        return HealthCheckProtocol.TryReceive(this.Socket, out message);
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
