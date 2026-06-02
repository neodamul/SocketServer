using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Sockets;
using SocketCommon.Model;

namespace SocketTests.Model;

[TestClass]
public class SocketAsyncEventArgsFactoryTests
{
    [TestMethod]
    public void InitialPoolSizeTest()
    {
        Assert.AreEqual(1000, SocketAsyncEventArgsFactory.InitialPoolSize);
        Assert.AreEqual(100, SocketAsyncEventArgsFactory.GrowthSize);
        Assert.IsTrue(SocketAsyncEventArgsFactory.TotalCreatedCount >= SocketAsyncEventArgsFactory.InitialPoolSize);
        Assert.IsTrue(SocketAsyncEventArgsFactory.GrowthCount >= 1);
    }

    [TestMethod]
    public void RentAndReturnTest()
    {
        int initialInUse = SocketAsyncEventArgsFactory.InUseCount;
        int initialRented = SocketAsyncEventArgsFactory.RentedCount;
        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();

        Assert.IsNotNull(args);
        Assert.AreEqual(initialInUse + 1, SocketAsyncEventArgsFactory.InUseCount);
        Assert.AreEqual(initialRented + 1, SocketAsyncEventArgsFactory.RentedCount);
        Assert.IsTrue(SocketAsyncEventArgsFactory.HighWatermarkInUseCount >= SocketAsyncEventArgsFactory.InUseCount);

        SocketAsyncEventArgsFactory.Return(args);
        Assert.AreEqual(initialInUse, SocketAsyncEventArgsFactory.InUseCount);
    }
}
