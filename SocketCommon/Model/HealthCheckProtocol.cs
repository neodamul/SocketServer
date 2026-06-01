using System;
using System.Net.Sockets;
using System.Text;

namespace SocketCommon.Model;

public enum HealthCheckMessageType
{
    Ping,
    Pong
}

public class HealthCheckMessage
{
    public HealthCheckMessage(HealthCheckMessageType type, string status = "")
    {
        this.Type = type;
        this.Status = status;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    public HealthCheckMessageType Type { get; }

    public string Status { get; }

    public DateTimeOffset CreatedAt { get; }
}

public static class HealthCheckProtocol
{
    public const string Name = "HEALTHCHECK";
    public const int Version = 1;
    public const int KeepAliveIntervalSeconds = 30;

    private const string PingMessage = "HEALTHCHECK/1 PING\n";
    private const string PongMessage = "HEALTHCHECK/1 PONG OK\n";
    private const int MaxMessageLength = 64;

    public static HealthCheckMessage CreatePing()
    {
        return new HealthCheckMessage(HealthCheckMessageType.Ping);
    }

    public static HealthCheckMessage CreatePong()
    {
        return new HealthCheckMessage(HealthCheckMessageType.Pong, "OK");
    }

    public static byte[] Encode(HealthCheckMessage message)
    {
        string text = message.Type switch
        {
            HealthCheckMessageType.Ping => PingMessage,
            HealthCheckMessageType.Pong => PongMessage,
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        };

        return Encoding.UTF8.GetBytes(text);
    }

    public static bool Send(Socket socket, HealthCheckMessage message)
    {
        try
        {
            byte[] bytes = Encode(message);
            int sent = socket.Send(bytes);
            return sent == bytes.Length;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public static bool TryReceive(Socket socket, out HealthCheckMessage message)
    {
        message = null;
        byte[] buffer = new byte[MaxMessageLength];
        int offset = 0;

        try
        {
            while (offset < buffer.Length)
            {
                int received = socket.Receive(buffer, offset, 1, SocketFlags.None);
                if (received == 0)
                {
                    return false;
                }

                if (buffer[offset] == (byte)'\n')
                {
                    string text = Encoding.UTF8.GetString(buffer, 0, offset + 1);
                    return TryDecode(text, out message);
                }

                offset += received;
            }
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return false;
    }

    public static bool TryDecode(byte[] bytes, out HealthCheckMessage message)
    {
        return TryDecode(Encoding.UTF8.GetString(bytes), out message);
    }

    public static bool TryDecode(string text, out HealthCheckMessage message)
    {
        message = null;
        string normalized = text.Trim();

        if (normalized == "HEALTHCHECK/1 PING")
        {
            message = CreatePing();
            return true;
        }

        if (normalized == "HEALTHCHECK/1 PONG OK")
        {
            message = CreatePong();
            return true;
        }

        return false;
    }
}
