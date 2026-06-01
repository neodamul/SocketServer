using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
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
}
