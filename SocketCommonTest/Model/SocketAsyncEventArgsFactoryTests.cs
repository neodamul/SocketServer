using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net.Sockets;
using SocketCommon.Model;

namespace SocketCommonTest.Model;

[TestClass]
public class SocketAsyncEventArgsFactoryTests
{
    [TestMethod]
    public void InitialPoolSizeTest()
    {
        Assert.AreEqual(1000, SocketAsyncEventArgsFactory.InitialPoolSize);
        Assert.AreEqual(100, SocketAsyncEventArgsFactory.GrowthSize);
    }

    [TestMethod]
    public void RentAndReturnTest()
    {
        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();

        Assert.IsNotNull(args);

        SocketAsyncEventArgsFactory.Return(args);
    }
}
