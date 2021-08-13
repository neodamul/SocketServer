using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketServer.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SocketServer.Model.Tests
{
    [TestClass()]
    public class TcpServerTests
    {
        [TestMethod()]
        public void StartTest()
        {
            TcpServer server = new();
            server.Start();
        }

        [TestMethod()]
        public void BindTest()
        {
            TcpServer server = new();
            server.Bind();
        }

        [TestMethod()]
        public void ListenTest()
        {
            TcpServer server = new();
            server.Listen();
        }

        [TestMethod()]
        public void EndTest()
        {
            TcpServer server = new();
            server.End();
        }
    }
}