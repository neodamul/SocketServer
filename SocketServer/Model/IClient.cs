using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SocketServer.Model
{
    public interface IClient
    {
        public void Initialize();

        public bool Connect();

        public bool Disconnect();

        public bool IsConnected();

        public void SetIpAddress(string ipAddress);

        public string GetIpAddress();

        public void SetPort(int port);

        public int GetPort();
    }
}
