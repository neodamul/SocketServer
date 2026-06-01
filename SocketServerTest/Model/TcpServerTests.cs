using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketServer.Model;

namespace SocketServer.Model.Tests;
[TestClass()]
public class TcpServerTests
{
    [TestMethod()]
    public void StartTest()
    {
        TcpServer server = new();
        Assert.IsTrue(server.Start());
        Assert.AreNotEqual(0, server.GetPort());
        server.End();
    }

    [TestMethod()]
    public void BindTest()
    {
        TcpServer server = new();
        Assert.IsTrue(server.Bind());
        Assert.AreNotEqual(0, server.GetPort());
        server.End();
    }

    [TestMethod()]
    public void ListenTest()
    {
        TcpServer server = new();
        Assert.IsTrue(server.Bind());
        Assert.IsTrue(server.Listen());
        server.End();
    }

    [TestMethod()]
    public void EndTest()
    {
        TcpServer server = new();
        Assert.IsTrue(server.Start());
        Assert.IsTrue(server.End());
    }
}
