using System;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SocketCommon.Model;

public static class ClientMessageIds
{
    public const uint ClientRegister = 2000;
    public const uint ClientRegisterAck = 2001;
    public const uint ClientMessageSend = 2002;
    public const uint ClientMessageDeliver = 2003;
    public const uint ClientMessageAck = 2004;
    public const uint ClientMessageError = 2005;
}

public static class ServerRelayMessageIds
{
    public const uint ServerRelayMessage = 2100;
    public const uint ServerRelayAck = 2101;
    public const uint ServerRelayError = 2102;
}

public class ClientRegisterRequest
{
    public uint ClientId { get; set; }

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ClientRegisterAck
{
    public uint ClientId { get; set; }

    public bool Success { get; set; }

    public string ErrorMessage { get; set; } = "";

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

    public int RetryAfterSeconds { get; set; }
}

public class ClientMessageSendRequest
{
    public string MessageToken { get; set; } = "";

    public uint SourceClientId { get; set; }

    public uint TargetClientId { get; set; }

    public string Content { get; set; } = "";

    public int TtlSeconds { get; set; } = 10;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ClientMessageDelivery
{
    public string MessageToken { get; set; } = "";

    public uint SourceClientId { get; set; }

    public uint TargetClientId { get; set; }

    public string Content { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ClientMessageAck
{
    public string MessageToken { get; set; } = "";

    public uint SourceClientId { get; set; }

    public uint TargetClientId { get; set; }

    public bool Delivered { get; set; }

    public string TargetInstanceId { get; set; } = "";

    public DateTimeOffset DeliveredAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ClientMessageError
{
    public string MessageToken { get; set; } = "";

    public uint SourceClientId { get; set; }

    public uint TargetClientId { get; set; }

    public string ErrorCode { get; set; } = "";

    public string ErrorMessage { get; set; } = "";

    public DateTimeOffset FailedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ServerRelayMessage
{
    public string ClusterId { get; set; } = "";

    public string SourceInstanceId { get; set; } = "";

    public string MessageToken { get; set; } = "";

    public uint SourceClientId { get; set; }

    public uint TargetClientId { get; set; }

    public string Content { get; set; } = "";

    public int TtlSeconds { get; set; } = 10;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class ClientMessageProtocol
{
    public static ClientMessageSendRequest CreateSendRequest(
        uint sourceClientId,
        uint targetClientId,
        string content,
        string messageToken = "",
        int ttlSeconds = 10)
    {
        return new ClientMessageSendRequest
        {
            MessageToken = string.IsNullOrWhiteSpace(messageToken) ? Guid.NewGuid().ToString("N") : messageToken,
            SourceClientId = sourceClientId,
            TargetClientId = targetClientId,
            Content = content ?? "",
            TtlSeconds = ttlSeconds <= 0 ? 10 : ttlSeconds,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static SocketMessageFrame CreateFrame<T>(uint clientId, uint messageId, T payload)
    {
        byte[] bytes = ProtobufPayloadSerializer.Encode(payload);
        return new SocketMessageFrame(clientId, messageId, bytes);
    }

    public static ClientMessageDelivery CreateDelivery(ClientMessageSendRequest request)
    {
        return new ClientMessageDelivery
        {
            MessageToken = request.MessageToken,
            SourceClientId = request.SourceClientId,
            TargetClientId = request.TargetClientId,
            Content = request.Content,
            CreatedAt = request.CreatedAt
        };
    }

    public static ServerRelayMessage CreateRelay(
        string clusterId,
        string sourceInstanceId,
        ClientMessageSendRequest request)
    {
        return new ServerRelayMessage
        {
            ClusterId = clusterId ?? "",
            SourceInstanceId = sourceInstanceId ?? "",
            MessageToken = request.MessageToken,
            SourceClientId = request.SourceClientId,
            TargetClientId = request.TargetClientId,
            Content = request.Content,
            TtlSeconds = request.TtlSeconds,
            CreatedAt = request.CreatedAt
        };
    }

    public static Task<bool> SendRegisterAsync(Socket socket, uint clientId)
    {
        return SendAsync(socket, clientId, ClientMessageIds.ClientRegister, new ClientRegisterRequest
        {
            ClientId = clientId
        });
    }

    public static Task<bool> SendRegisterAsync(SecureSocketConnection connection, uint clientId)
    {
        return SendAsync(connection, clientId, ClientMessageIds.ClientRegister, new ClientRegisterRequest
        {
            ClientId = clientId
        });
    }

    public static Task<bool> SendClientMessageAsync(Socket socket, ClientMessageSendRequest request)
    {
        return SendAsync(socket, request.SourceClientId, ClientMessageIds.ClientMessageSend, request);
    }

    public static Task<bool> SendClientMessageAsync(SecureSocketConnection connection, ClientMessageSendRequest request)
    {
        return SendAsync(connection, request.SourceClientId, ClientMessageIds.ClientMessageSend, request);
    }

    public static Task<bool> SendDeliveryAsync(Socket socket, ClientMessageDelivery delivery)
    {
        return SendAsync(socket, delivery.TargetClientId, ClientMessageIds.ClientMessageDeliver, delivery);
    }

    public static Task<bool> SendAckAsync(Socket socket, ClientMessageAck ack)
    {
        return SendAsync(socket, ack.SourceClientId, ClientMessageIds.ClientMessageAck, ack);
    }

    public static Task<bool> SendErrorAsync(Socket socket, ClientMessageError error)
    {
        return SendAsync(socket, error.SourceClientId, ClientMessageIds.ClientMessageError, error);
    }

    public static Task<bool> SendRelayAsync(Socket socket, ServerRelayMessage relay)
    {
        return SendAsync(socket, relay.SourceClientId, ServerRelayMessageIds.ServerRelayMessage, relay);
    }

    public static Task<bool> SendRelayAsync(SecureSocketConnection connection, ServerRelayMessage relay)
    {
        return SendAsync(connection, relay.SourceClientId, ServerRelayMessageIds.ServerRelayMessage, relay);
    }

    public static bool TryDecodeRegister(SocketMessageFrame frame, out ClientRegisterRequest request)
    {
        return TryDecode(frame, ClientMessageIds.ClientRegister, out request);
    }

    public static bool TryDecodeRegisterAck(SocketMessageFrame frame, out ClientRegisterAck ack)
    {
        return TryDecode(frame, ClientMessageIds.ClientRegisterAck, out ack);
    }

    public static bool TryDecodeSendRequest(SocketMessageFrame frame, out ClientMessageSendRequest request)
    {
        return TryDecode(frame, ClientMessageIds.ClientMessageSend, out request);
    }

    public static bool TryDecodeDelivery(SocketMessageFrame frame, out ClientMessageDelivery delivery)
    {
        return TryDecode(frame, ClientMessageIds.ClientMessageDeliver, out delivery);
    }

    public static bool TryDecodeAck(SocketMessageFrame frame, out ClientMessageAck ack)
    {
        return TryDecode(frame, ClientMessageIds.ClientMessageAck, out ack);
    }

    public static bool TryDecodeError(SocketMessageFrame frame, out ClientMessageError error)
    {
        return TryDecode(frame, ClientMessageIds.ClientMessageError, out error);
    }

    public static bool TryDecodeRelay(SocketMessageFrame frame, out ServerRelayMessage relay)
    {
        return TryDecode(frame, ServerRelayMessageIds.ServerRelayMessage, out relay);
    }

    public static async Task<(bool Success, SocketMessageFrame Frame)> SendRelayAndReceiveAsync(
        Socket socket,
        ServerRelayMessage relay,
        int timeoutMilliseconds = 0)
    {
        if (!await SendRelayAsync(socket, relay))
        {
            return (false, null);
        }

        timeoutMilliseconds = NormalizeTimeout(timeoutMilliseconds);
        Task<(bool Success, SocketMessageFrame Frame)> receiveTask = SocketMessageFrame.TryReceiveAsync(socket);
        Task completedTask = await Task.WhenAny(receiveTask, Task.Delay(timeoutMilliseconds));
        if (completedTask != receiveTask)
        {
            return (false, null);
        }

        return await receiveTask;
    }

    public static async Task<(bool Success, SocketMessageFrame Frame)> SendRelayAndReceiveAsync(
        SecureSocketConnection connection,
        ServerRelayMessage relay,
        int timeoutMilliseconds = 0)
    {
        if (!await SendRelayAsync(connection, relay))
        {
            return (false, null);
        }

        timeoutMilliseconds = NormalizeTimeout(timeoutMilliseconds);
        Task<(bool Success, SocketMessageFrame Frame)> receiveTask = SocketMessageFrame.TryReceiveAsync(connection);
        Task completedTask = await Task.WhenAny(receiveTask, Task.Delay(timeoutMilliseconds));
        if (completedTask != receiveTask)
        {
            return (false, null);
        }

        return await receiveTask;
    }

    private static int NormalizeTimeout(int timeoutMilliseconds)
    {
        return timeoutMilliseconds <= 0
            ? SocketFactory.ReadTimeoutMilliseconds
            : timeoutMilliseconds;
    }

    public static bool TryDecode<T>(SocketMessageFrame frame, uint expectedMessageId, out T payload)
    {
        payload = default;
        if (frame == null || frame.MessageId != expectedMessageId)
        {
            return false;
        }

        return ProtobufPayloadSerializer.TryDecode(frame.Payload, out payload);
    }

    private static Task<bool> SendAsync<T>(Socket socket, uint clientId, uint messageId, T payload)
    {
        return SocketMessageFrame.SendAsync(socket, CreateFrame(clientId, messageId, payload));
    }

    private static Task<bool> SendAsync<T>(SecureSocketConnection connection, uint clientId, uint messageId, T payload)
    {
        return SocketMessageFrame.SendAsync(connection, CreateFrame(clientId, messageId, payload));
    }
}
