using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SocketCommon.Model;

public class SocketMessageFrame
{
    public const int HeaderLength = 12;
    public const int MaxPayloadLength = 4 * 1024;

    public SocketMessageFrame(uint clientId, uint messageId, byte[] payload)
        : this(clientId, messageId, payload, skipPayloadLimitValidation: false)
    {
    }

    internal SocketMessageFrame(uint clientId, uint messageId, byte[] payload, bool skipPayloadLimitValidation)
    {
        this.ClientId = clientId;
        this.MessageId = messageId;
        this.Payload = payload ?? Array.Empty<byte>();
        if (!skipPayloadLimitValidation && this.Payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }
    }

    public uint ClientId { get; }

    public uint MessageId { get; }

    public byte[] Payload { get; }

    public byte[] Encode()
    {
        byte[] bytes = new byte[HeaderLength + this.Payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), this.ClientId);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), this.MessageId);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), (uint)this.Payload.Length);
        this.Payload.CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }

    public static bool TryDecode(byte[] bytes, out SocketMessageFrame frame)
    {
        frame = null;
        if (bytes == null || bytes.Length < HeaderLength)
        {
            return false;
        }

        uint clientId = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        uint messageId = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
        if (payloadLength > MaxPayloadLength || bytes.Length != HeaderLength + payloadLength)
        {
            return false;
        }

        byte[] payload = new byte[payloadLength];
        bytes.AsSpan(HeaderLength).CopyTo(payload);
        frame = new SocketMessageFrame(clientId, messageId, payload);
        return true;
    }

    public static Task<bool> SendAsync(Socket socket, SocketMessageFrame frame)
    {
        return SocketAsyncEventArgsTransport.SendAsync(socket, frame.Encode());
    }

    public static Task<bool> SendAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        return connection.SendAsync(frame.Encode());
    }

    public static async Task<(bool Success, SocketMessageFrame Frame)> TryReceiveAsync(Socket socket)
    {
        byte[] header = await SocketAsyncEventArgsTransport.ReceiveExactAsync(socket, HeaderLength);
        if (header == null)
        {
            return (false, null);
        }

        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (payloadLength > MaxPayloadLength)
        {
            return (false, null);
        }

        byte[] payload = payloadLength == 0
            ? Array.Empty<byte>()
            : await SocketAsyncEventArgsTransport.ReceiveExactAsync(socket, (int)payloadLength);
        if (payload == null)
        {
            return (false, null);
        }

        return (true, new SocketMessageFrame(
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)),
            payload));
    }

    public static async Task<(bool Success, SocketMessageFrame Frame)> TryReceiveAsync(SecureSocketConnection connection)
    {
        return await TryReceiveAsync(
            connection,
            SocketFactory.ReadTimeoutMilliseconds,
            SocketFactory.ReadTimeoutMilliseconds);
    }

    public static async Task<(bool Success, SocketMessageFrame Frame)> TryReceiveAsync(
        SecureSocketConnection connection,
        int headerTimeoutMilliseconds,
        int payloadTimeoutMilliseconds)
    {
        byte[] header = await connection.ReceiveExactAsync(HeaderLength, headerTimeoutMilliseconds);
        if (header == null)
        {
            return (false, null);
        }

        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (payloadLength > MaxPayloadLength)
        {
            return (false, null);
        }

        byte[] payload = payloadLength == 0
            ? Array.Empty<byte>()
            : await connection.ReceiveExactAsync((int)payloadLength, payloadTimeoutMilliseconds);
        if (payload == null)
        {
            return (false, null);
        }

        return (true, new SocketMessageFrame(
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)),
            payload));
    }
}
