using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SocketCommon.Model;

public enum HealthCheckMessageType
{
    Ping,
    Pong
}

public class HealthCheckMessage
{
    public HealthCheckMessage(uint clientId, HealthCheckMessageType type, string status = "")
    {
        this.ClientId = clientId;
        this.Type = type;
        this.Status = status;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    public uint ClientId { get; }

    public HealthCheckMessageType Type { get; }

    public string Status { get; }

    public DateTimeOffset CreatedAt { get; }
}

public static class HealthCheckProtocol
{
    public const string Name = "HEALTHCHECK";
    public const int Version = 1;
    public const int KeepAliveIntervalSeconds = 30;
    public const uint PingMessageId = 1;
    public const uint PongMessageId = 2;

    private const string PongPayload = "OK";

    public static HealthCheckMessage CreatePing(uint clientId = 0)
    {
        return new HealthCheckMessage(clientId, HealthCheckMessageType.Ping);
    }

    public static HealthCheckMessage CreatePong(uint clientId = 0)
    {
        return new HealthCheckMessage(clientId, HealthCheckMessageType.Pong, PongPayload);
    }

    public static byte[] Encode(HealthCheckMessage message)
    {
        return CreateFrame(message).Encode();
    }

    public static bool Send(Socket socket, HealthCheckMessage message)
    {
        return SendAsync(socket, message).GetAwaiter().GetResult();
    }

    public static Task<bool> SendAsync(Socket socket, HealthCheckMessage message)
    {
        return SocketMessageFrame.SendAsync(socket, CreateFrame(message));
    }

    public static Task<bool> SendAsync(SecureSocketConnection connection, HealthCheckMessage message)
    {
        return SocketMessageFrame.SendAsync(connection, CreateFrame(message));
    }

    public static bool TryReceive(Socket socket, out HealthCheckMessage message)
    {
        (bool success, HealthCheckMessage receivedMessage) = TryReceiveAsync(socket).GetAwaiter().GetResult();
        message = receivedMessage;
        return success;
    }

    public static async Task<(bool Success, HealthCheckMessage Message)> TryReceiveAsync(Socket socket)
    {
        (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(socket);
        if (!success || !TryDecode(frame, out HealthCheckMessage message))
        {
            return (false, null);
        }

        return (true, message);
    }

    public static async Task<(bool Success, HealthCheckMessage Message)> TryReceiveAsync(SecureSocketConnection connection)
    {
        (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(connection);
        if (!success || !TryDecode(frame, out HealthCheckMessage message))
        {
            return (false, null);
        }

        return (true, message);
    }

    public static bool TryDecode(byte[] bytes, out HealthCheckMessage message)
    {
        message = null;
        if (!SocketMessageFrame.TryDecode(bytes, out SocketMessageFrame frame))
        {
            return false;
        }

        return TryDecode(frame, out message);
    }

    public static bool TryDecode(SocketMessageFrame frame, out HealthCheckMessage message)
    {
        message = null;

        if (frame.MessageId == PingMessageId && frame.Payload.Length == 0)
        {
            message = CreatePing(frame.ClientId);
            return true;
        }

        if (frame.MessageId == PongMessageId)
        {
            string status = Encoding.UTF8.GetString(frame.Payload);
            if (status != PongPayload)
            {
                return false;
            }

            message = CreatePong(frame.ClientId);
            return true;
        }

        return false;
    }

    private static SocketMessageFrame CreateFrame(HealthCheckMessage message)
    {
        uint messageId = message.Type switch
        {
            HealthCheckMessageType.Ping => PingMessageId,
            HealthCheckMessageType.Pong => PongMessageId,
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        };

        byte[] payload = message.Type == HealthCheckMessageType.Pong
            ? Encoding.UTF8.GetBytes(message.Status)
            : Array.Empty<byte>();

        return new SocketMessageFrame(message.ClientId, messageId, payload);
    }
}
