using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Sockets;
using SocketCommon.Configuration;
using SocketCommon.Model;

namespace SocketTests.Model;

[TestClass]
public class SocketFactoryTests
{
    [TestCleanup]
    public void Cleanup()
    {
        SocketFactory.Configure(new SocketOperationConfig());
    }

    [TestMethod]
    public void CreateTcpSocketAppliesDefaultOptionsTest()
    {
        using Socket socket = SocketFactory.CreateTcpSocket();

        Assert.AreEqual(SocketFactory.ListenBacklog, 100);
        Assert.IsTrue(socket.NoDelay);
    }

    [TestMethod]
    public void SocketOperationTimeoutsHaveDefaultThirtySecondsTest()
    {
        SocketFactory.Configure(new SocketOperationConfig());

        Assert.AreEqual(30000, SocketFactory.ConnectTimeoutMilliseconds);
        Assert.AreEqual(30000, SocketFactory.ReadTimeoutMilliseconds);
        Assert.AreEqual(30000, SocketFactory.WriteTimeoutMilliseconds);
    }

    [TestMethod]
    public void SocketOperationTimeoutsCanBeConfiguredTest()
    {
        SocketFactory.Configure(new SocketOperationConfig
        {
            ConnectTimeoutSeconds = 3,
            ReadTimeoutSeconds = 5,
            WriteTimeoutSeconds = 7
        });

        Assert.AreEqual(3000, SocketFactory.ConnectTimeoutMilliseconds);
        Assert.AreEqual(5000, SocketFactory.ReadTimeoutMilliseconds);
        Assert.AreEqual(7000, SocketFactory.WriteTimeoutMilliseconds);
    }

    [TestMethod]
    public void InvalidSocketOperationTimeoutsFallbackToDefaultTest()
    {
        SocketFactory.Configure(new SocketOperationConfig
        {
            ConnectTimeoutSeconds = 0,
            ReadTimeoutSeconds = -1,
            WriteTimeoutSeconds = -30
        });

        Assert.AreEqual(30000, SocketFactory.ConnectTimeoutMilliseconds);
        Assert.AreEqual(30000, SocketFactory.ReadTimeoutMilliseconds);
        Assert.AreEqual(30000, SocketFactory.WriteTimeoutMilliseconds);
    }
}
