using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SocketServer.Model
{
    public interface IServer : IClient
    {

        public bool Start();

        public bool End();

        public bool Bind();

        public bool Listen();
    }
}
