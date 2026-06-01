using System;
using log4net;

namespace SocketCommon.Logging;

public static class SocketLogManager
{
    public static SocketLogger GetLogger<T>()
    {
        return GetLogger(typeof(T));
    }

    public static SocketLogger GetLogger(Type type)
    {
        LogConfigurator.Configure();
        return new SocketLogger(LogManager.GetLogger(type));
    }
}

public sealed class SocketLogger
{
    private readonly ILog log;

    internal SocketLogger(ILog log)
    {
        this.log = log;
    }

    public void Debug(string message)
    {
        this.log.Debug(message);
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
