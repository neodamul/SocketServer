using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketCommon.Configuration;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class SocketConfigurationTests
{
    [TestMethod]
    public void ControlServerDefaultsUseHighLoadRouteReservationTest()
    {
        ControlServerNodeConfig config = new();

        Assert.AreEqual(60, config.RouteReservationSeconds);
        Assert.AreEqual(ControlServerNodeConfig.DefaultRouteReservationSeconds, config.RouteReservationSeconds);
    }

    [TestMethod]
    public void SocketServerDefaultsUseHighLoadAcceptWorkersTest()
    {
        SocketServerInstanceConfig config = new();

        Assert.AreEqual(512, config.PendingAcceptCount);
        Assert.AreEqual(SocketServerInstanceConfig.DefaultPendingAcceptCount, config.PendingAcceptCount);
        Assert.AreEqual(SocketServerInstanceConfig.DefaultPendingAcceptCount, TcpServer.DefaultPendingAcceptCount);
    }

    [TestMethod]
    public void CheckedInControlServerConfigBindsAdmissionSettingsTest()
    {
        string path = Path.Combine(FindRepositoryRoot(), "SocketControl/config.json");

        ControlServerConfigFile config = SocketConfigLoader.Load<ControlServerConfigFile>(path);

        Assert.AreEqual(4096, config.SocketOptions.ListenBacklog);
        Assert.AreEqual(60, config.ControlServer.RouteReservationSeconds);
    }

    [TestMethod]
    public void CheckedInSocketServerConfigBindsAdmissionSettingsTest()
    {
        string path = Path.Combine(FindRepositoryRoot(), "SocketServer/config.json");

        SocketServerConfigFile config = SocketConfigLoader.Load<SocketServerConfigFile>(path);

        Assert.AreEqual(4096, config.SocketOptions.ListenBacklog);
        Assert.AreEqual(1, config.Servers.Count);
        Assert.AreEqual(512, config.Servers[0].PendingAcceptCount);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SocketServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
