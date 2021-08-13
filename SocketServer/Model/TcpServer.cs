using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;

namespace SocketServer.Model
{
    public class TcpServer : TcpClient, IServer, IClient
    {
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
            return true;
        }

        public bool Bind()
        {
            return true;
        }

        public bool Listen()
        {
            return true;
        }

        public bool End()
        {
            return true;
        }

    }
}