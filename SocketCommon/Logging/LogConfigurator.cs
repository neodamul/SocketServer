using System;
using System.IO;
using System.Reflection;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;

namespace SocketCommon.Logging;

public static class LogConfigurator
{
    private static readonly object SyncRoot = new();
    private static bool configured;

    public static void Configure(string configFileName = "log4net.config")
    {
        lock (SyncRoot)
        {
            if (configured)
            {
                return;
            }

            EnsureLogDirectories();
            FileInfo configFile = new(Path.Combine(AppContext.BaseDirectory, configFileName));
            if (configFile.Exists)
            {
                XmlConfigurator.Configure(GetRepository(), configFile);
            }
            else
            {
                ConfigureFallback(GetRepository());
            }

            configured = true;
        }
    }

    private static void ConfigureFallback(log4net.Repository.ILoggerRepository repository)
    {
        PatternLayout layout = CreateLayout();
        ConsoleAppender consoleAppender = new()
        {
            Layout = layout,
            Threshold = Level.Info
        };
        consoleAppender.ActivateOptions();

        RollingFileAppender generalAppender = CreateRollingFileAppender("FallbackRollingFileAppender", "logs/socket.log");
        BasicConfigurator.Configure(repository, consoleAppender, generalAppender);

        if (repository is not Hierarchy hierarchy)
        {
            return;
        }

        RollingFileAppender relayAppender = CreateRollingFileAppender("FallbackRelayRollingFileAppender", "logs/socket.relay.log");
        Logger relayLogger = (Logger)hierarchy.GetLogger("SocketRelay");
        relayLogger.Level = Level.Debug;
        relayLogger.Additivity = false;
        relayLogger.AddAppender(relayAppender);
        hierarchy.Configured = true;
    }

    private static PatternLayout CreateLayout()
    {
        PatternLayout layout = new("%date %-5level [%thread] %logger - %message%newline%exception");
        layout.ActivateOptions();
        return layout;
    }

    private static RollingFileAppender CreateRollingFileAppender(string name, string file)
    {
        RollingFileAppender appender = new()
        {
            Name = name,
            File = file,
            AppendToFile = true,
            RollingStyle = RollingFileAppender.RollingMode.Size,
            MaxSizeRollBackups = 5,
            MaximumFileSize = "10MB",
            StaticLogFileName = true,
            Layout = CreateLayout()
        };
        appender.ActivateOptions();
        return appender;
    }

    private static void EnsureLogDirectories()
    {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "logs"));
    }

    private static log4net.Repository.ILoggerRepository GetRepository()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(LogConfigurator).Assembly;
        return LogManager.GetRepository(assembly);
    }
}
