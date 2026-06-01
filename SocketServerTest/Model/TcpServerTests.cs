using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketServer.Model;

namespace SocketServer.Model.Tests;
[TestClass()]
public class TcpServerTests
{
    private const int TestPort = 5001;

    [TestMethod()]
    public void StartTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Start());
        Assert.AreEqual(TestPort, server.GetPort());
        server.End();
    }

    [TestMethod()]
    public void BindTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Bind());
        Assert.AreEqual(TestPort, server.GetPort());
        server.End();
    }

    [TestMethod()]
    public void ListenTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Bind());
        Assert.IsTrue(server.Listen());
        server.End();
    }

    [TestMethod()]
    public void EndTest()
    {
        TcpServer server = new(1, "testServer", "127.0.0.1", TestPort);
        Assert.IsTrue(server.Start());
        Assert.IsTrue(server.End());
    }
}
