using System;
using log4net;

namespace SocketCommon.Logging;

public static class SocketLogManager
{
    private const string RelayLoggerPrefix = "SocketRelay";

    public static SocketLogger GetLogger<T>()
    {
        return GetLogger(typeof(T));
    }

    public static SocketLogger GetLogger(Type type)
    {
        LogConfigurator.Configure();
        return new SocketLogger(LogManager.GetLogger(type));
    }

    public static SocketLogger GetRelayLogger<T>()
    {
        return GetRelayLogger(typeof(T));
    }

    public static SocketLogger GetRelayLogger(Type type)
    {
        LogConfigurator.Configure();
        return new SocketLogger(LogManager.GetLogger($"{RelayLoggerPrefix}.{type.FullName}"));
    }
}

public sealed class SocketLogger
{
    private readonly ILog log;

    internal SocketLogger(ILog log)
    {
        this.log = log;
    }

    public bool IsDebugEnabled => this.log.IsDebugEnabled;

    public void Debug(string message)
    {
        this.log.Debug(message);
    }

    public void Debug(Func<string> messageFactory)
    {
        if (!this.log.IsDebugEnabled)
        {
            return;
        }

        this.log.Debug(messageFactory());
    }

    public void Info(string message)
    {
        this.log.Info(message);
    }

    public void Warn(string message)
    {
        this.log.Warn(message);
    }

    public void Warn(string message, Exception exception)
    {
        this.log.Warn(message, exception);
    }

    public void Error(string message, Exception exception)
    {
        this.log.Error(message, exception);
    }
}
