using System;
using System.Net;
using System.Net.Sockets;
using SocketCommon;
using SocketCommon.Interface;
using SocketCommon.Model;

namespace SocketServer.Model;
public class TcpServer : SocketClient.Model.TcpClient, IServer, IClient, IDisposable
{
    private bool disposedValue;

    public TcpServer() : base(0, null)
    { }

    public TcpServer(int id, string name)
        : base(id, name, null, Constants.LocalHostPort)
    { }

    public TcpServer(int id, string name, string ipAddress, int port)
        : base(id, name, ipAddress, port)
    { }

    public bool Start()
    {
        return this.Bind() && this.Listen();
    }

    public bool Bind()
    {
        try
        {
            if (this.Socket == null)
            {
                this.Initialize();
            }

            this.Socket.Bind(new IPEndPoint(this.IpAddress, this.Port));
            if (this.Socket.LocalEndPoint is IPEndPoint localEndPoint)
            {
                this.SetPort(localEndPoint.Port);
            }

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

    public bool Listen()
    {
        try
        {
            if (this.Socket == null)
            {
                return false;
            }

            this.Socket.Listen(100);
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

    public bool End()
    {
        this.Disconnect();
        return true;
    }

    public bool AcceptHelloWorldRequestAndRespond()
    {
        if (this.Socket == null)
        {
            return false;
        }

        try
        {
            using Socket client = this.Socket.Accept();
            if (!HelloWorldProtocol.TryReceiveRequest(client, out HelloWorldRequest request))
            {
                return false;
            }

            return HelloWorldProtocol.Send(client, HelloWorldProtocol.CreateResponse());
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

    protected override void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                this.End();
            }

            base.Dispose(disposing);
            disposedValue = true;
        }
    }

    // // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
    // ~TcpServer()
    // {
    //     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
    //     Dispose(disposing: false);
    // }

    void IDisposable.Dispose()
    {
        // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
