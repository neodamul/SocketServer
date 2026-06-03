using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SocketCommon.Logging;

namespace SocketTests.Model;

[TestClass]
public class SocketLoggingTests
{
    [TestMethod]
    public void LoggerCanWriteMessagesTest()
    {
        LogConfigurator.Configure();
        SocketLogger logger = SocketLogManager.GetLogger<SocketLoggingTests>();

        logger.Info("Socket logging test message.");
        logger.Warn("Socket logging warning test message.");

        Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "log4net.config")));
    }

    [TestMethod]
    public void RelayLoggerCanWriteMessagesTest()
    {
        LogConfigurator.Configure();
        SocketLogger relayLogger = SocketLogManager.GetRelayLogger<SocketLoggingTests>();

        relayLogger.Info("Socket relay logging test message.");
        relayLogger.Debug("Socket relay logging debug test message.");

        Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "log4net.config")));
    }

    [TestMethod]
    public void ProjectLogConfigurationsContainSeparateRelayAppenderTest()
    {
        string solutionRoot = FindSolutionRoot();
        string[] configFiles =
        {
            "SocketCommon/log4net.config",
            "SocketClient/log4net.config",
            "SocketControl/log4net.config",
            "SocketServer/log4net.config",
            "SocketDashboard/log4net.config",
            "SocketLoadTest/log4net.config",
            "SocketLoadTest/log4net.load-test.config",
            "SocketTests/log4net.config"
        };

        foreach (string relativePath in configFiles)
        {
            string path = Path.Combine(solutionRoot, relativePath);
            Assert.IsTrue(File.Exists(path), $"Missing log4net config: {relativePath}");

            XDocument document = XDocument.Load(path);
            XElement root = document.Root!;
            IEnumerable<XElement> appenders = root.Elements("appender");
            XElement? relayAppender = appenders.SingleOrDefault(item =>
                string.Equals((string?)item.Attribute("name"), "RelayRollingFileAppender", StringComparison.Ordinal));
            Assert.IsNotNull(relayAppender, $"Relay appender is missing: {relativePath}");
            Assert.IsTrue(
                ((string?)relayAppender!.Element("file")?.Attribute("value"))?.Contains(".relay.log", StringComparison.OrdinalIgnoreCase) == true,
                $"Relay appender must write to a relay log file: {relativePath}");

            XElement? relayLogger = root.Elements("logger").SingleOrDefault(item =>
                string.Equals((string?)item.Attribute("name"), "SocketRelay", StringComparison.Ordinal));
            Assert.IsNotNull(relayLogger, $"SocketRelay logger is missing: {relativePath}");
            Assert.AreEqual("false", (string?)relayLogger!.Attribute("additivity"), $"SocketRelay must not write through the root logger: {relativePath}");
            Assert.AreEqual(
                "DEBUG",
                (string?)relayLogger.Element("level")?.Attribute("value"),
                $"SocketRelay must keep DEBUG relay traces: {relativePath}");
        }
    }

    private static string FindSolutionRoot()
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

        throw new DirectoryNotFoundException("SocketServer.sln was not found from the test output path.");
    }
}
