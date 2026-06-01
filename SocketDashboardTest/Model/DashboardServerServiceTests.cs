using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketCommon.Model;
using SocketDashboard.Model;

namespace SocketDashboardTest.Model;

[TestClass]
public class DashboardServerServiceTests
{
    [TestMethod]
    public void GetStatusReturnsRunningServerStatusTest()
    {
        using DashboardServerService service = new(0);

        DashboardServerStatus status = service.GetStatus();

        Assert.IsTrue(status.StartSucceeded);
        Assert.IsTrue(status.Server.IsSocketInitialized);
        Assert.IsTrue(status.Server.IsBound);
        Assert.IsTrue(status.Server.IsListening);
        Assert.IsTrue(status.Server.IsAcceptLoopRunning);
        Assert.AreEqual(SocketFactory.ListenBacklog, status.Server.ListenBacklog);
        Assert.AreEqual(SocketFactory.NoDelay, status.Server.NoDelay);
        Assert.AreEqual(SocketMessageFrame.MaxPayloadLength, status.Server.MaxPayloadLength);
        Assert.IsTrue(status.Server.Port > 0);
    }
}
