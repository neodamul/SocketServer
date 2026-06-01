using System;
using System.IO;
using System.Reflection;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;

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
                PatternLayout layout = new("%date %-5level [%thread] %logger - %message%newline%exception");
                layout.ActivateOptions();

                ConsoleAppender appender = new()
                {
                    Layout = layout
                };
                appender.ActivateOptions();

                BasicConfigurator.Configure(GetRepository(), appender);
            }

            configured = true;
        }
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
