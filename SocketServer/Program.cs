using System;
using SocketServer.Model;

namespace SocketServer;
class Program
{
    static void Main(string[] args)
    {
        TcpClient tcpClient = new(1, "testClient");
        Console.WriteLine(tcpClient);

        TcpServer tcpServer = new(1, "testServer");
        Console.WriteLine(tcpServer);
    }
}