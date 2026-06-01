using System;
using SocketClient.Model;
using SocketCommon.Logging;
using SocketServer.Model;

namespace SocketServer;
class Program
{
    static void Main(string[] args)
    {
        LogConfigurator.Configure();
        SocketLogger logger = SocketLogManager.GetLogger<Program>();
        logger.Info("SocketServer console starting.");

        TcpClient tcpClient = new(1, "testClient");
        Console.WriteLine(tcpClient);

        TcpServer tcpServer = new(1, "testServer");
        Console.WriteLine(tcpServer);
    }
}
