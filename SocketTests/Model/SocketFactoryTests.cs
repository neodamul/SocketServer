using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Sockets;
using SocketCommon.Model;

namespace SocketTests.Model;

[TestClass]
public class SocketFactoryTests
{
    [TestMethod]
    public void CreateTcpSocketAppliesDefaultOptionsTest()
    {
        using Socket socket = SocketFactory.CreateTcpSocket();

        Assert.AreEqual(SocketFactory.ListenBacklog, 100);
        Assert.IsTrue(socket.NoDelay);
    }
}
