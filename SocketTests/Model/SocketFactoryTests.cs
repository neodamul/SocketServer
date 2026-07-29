using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
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

        Assert.AreEqual(SocketFactory.DefaultListenBacklog, SocketFactory.ListenBacklog);
        Assert.IsTrue(socket.NoDelay);
    }

    [TestMethod]
    public void BindSourceAddressUsesRequestedLocalAddressTest()
    {
        using Socket socket = SocketFactory.CreateTcpSocket();

        SocketFactory.BindSourceAddress(socket, IPAddress.Loopback);

        Assert.AreEqual(IPAddress.Loopback, ((IPEndPoint)socket.LocalEndPoint!).Address);
    }

    [TestMethod]
    public async Task CreateTcpSocketRejectsDuplicateListenerOnSamePortTest()
    {
        using Socket first = SocketFactory.CreateTcpSocket();
        first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        first.Listen(SocketFactory.ListenBacklog);
        int port = ((IPEndPoint)first.LocalEndPoint!).Port;

        using Socket second = SocketFactory.CreateTcpSocket();
        await Assert.ThrowsExceptionAsync<SocketException>(() =>
        {
            second.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void SocketOperationTimeoutsHaveDefaultThirtySecondsTest()
    {
        SocketFactory.Configure(new SocketOperationConfig());

        Assert.AreEqual(30000, SocketFactory.ConnectTimeoutMilliseconds);
        Assert.AreEqual(30000, SocketFactory.ReadTimeoutMilliseconds);
        Assert.AreEqual(30000, SocketFactory.WriteTimeoutMilliseconds);
        Assert.AreEqual(SocketFactory.DefaultListenBacklog, SocketFactory.ListenBacklog);
    }

    [TestMethod]
    public void SocketOperationTimeoutsCanBeConfiguredTest()
    {
        SocketFactory.Configure(new SocketOperationConfig
        {
            ConnectTimeoutSeconds = 3,
            ReadTimeoutSeconds = 5,
            WriteTimeoutSeconds = 7,
            ListenBacklog = 4096
        });

        Assert.AreEqual(3000, SocketFactory.ConnectTimeoutMilliseconds);
        Assert.AreEqual(5000, SocketFactory.ReadTimeoutMilliseconds);
        Assert.AreEqual(7000, SocketFactory.WriteTimeoutMilliseconds);
        Assert.AreEqual(4096, SocketFactory.ListenBacklog);
    }

    [TestMethod]
    public void InvalidSocketOperationTimeoutsFallbackToDefaultTest()
    {
        SocketFactory.Configure(new SocketOperationConfig
        {
            ConnectTimeoutSeconds = 0,
            ReadTimeoutSeconds = -1,
            WriteTimeoutSeconds = -30,
            ListenBacklog = 0
        });

        Assert.AreEqual(30000, SocketFactory.ConnectTimeoutMilliseconds);
        Assert.AreEqual(30000, SocketFactory.ReadTimeoutMilliseconds);
        Assert.AreEqual(30000, SocketFactory.WriteTimeoutMilliseconds);
        Assert.AreEqual(SocketFactory.DefaultListenBacklog, SocketFactory.ListenBacklog);
    }

    [TestMethod]
    public async Task ResolveAddressAsyncSupportsDnsHostTest()
    {
        IPAddress address = await SocketFactory.ResolveAddressAsync("localhost", AddressFamily.InterNetwork);

        Assert.AreEqual(AddressFamily.InterNetwork, address.AddressFamily);
    }

    [TestMethod]
    public void SelectAddressRejectsAddressFamilyMismatchTest()
    {
        SocketException exception = Assert.ThrowsException<SocketException>(() =>
            SocketFactory.SelectAddress(
                "ipv6-only.example",
                new[] { IPAddress.IPv6Loopback },
                AddressFamily.InterNetwork));

        Assert.AreEqual(SocketError.AddressFamilyNotSupported, exception.SocketErrorCode);
    }
}
