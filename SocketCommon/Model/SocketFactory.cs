using System.Net.Sockets;

namespace SocketCommon.Model;

public static class SocketFactory
{
    public const int ListenBacklog = 100;
    public const bool NoDelay = true;

    public static Socket CreateTcpSocket(AddressFamily family = AddressFamily.InterNetwork)
    {
        Socket socket = new(family, SocketType.Stream, ProtocolType.Tcp);
        ConfigureTcpSocket(socket);
        return socket;
    }

    public static void ConfigureTcpSocket(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.NoDelay = NoDelay;
    }
}
