using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using SocketServer.Interface;

namespace SocketServer.Model;
public class TcpClient : IClient, IDisposable
{
    private Socket Socket = null;

    private bool disposedValue;

    private int Id { get; set; }
    private string Name { get; set; }
    private AddressFamily Family { get; set; }
    private IPAddress IpAddress { get; set; }
    private int Port { get; set; }

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
        this.Socket = new Socket(this.Family, SocketType.Stream, ProtocolType.Tcp);
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
        return true;
    }

    public bool Disconnect()
    {
        return true;
    }

    public bool IsConnected()
    {
        return true;
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
                // TODO: 관리형 상태(관리형 개체)를 삭제합니다.
            }

            // TODO: 비관리형 리소스(비관리형 개체)를 해제하고 종료자를 재정의합니다.
            // TODO: 큰 필드를 null로 설정합니다.
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