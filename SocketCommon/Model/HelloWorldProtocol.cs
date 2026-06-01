using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SocketCommon.Model;

public class HelloWorldRequest
{
    public HelloWorldRequest(uint clientId = 0)
    {
        this.ClientId = clientId;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    public uint ClientId { get; }

    public DateTimeOffset CreatedAt { get; }
}

public class HelloWorldResponse
{
    public HelloWorldResponse(uint clientId = 0, string message = HelloWorldProtocol.DefaultMessage)
    {
        this.ClientId = clientId;
        this.Message = message;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    public uint ClientId { get; }

    public string Message { get; }

    public DateTimeOffset CreatedAt { get; }
}

public static class HelloWorldProtocol
{
    public const string Name = "HELLOWORLD";
    public const int Version = 1;
    public const string DefaultMessage = "Hello, World!";
    public const uint RequestMessageId = 100;
    public const uint ResponseMessageId = 101;

    public static HelloWorldRequest CreateRequest(uint clientId = 0)
    {
        return new HelloWorldRequest(clientId);
    }

    public static HelloWorldResponse CreateResponse(uint clientId = 0)
    {
        return new HelloWorldResponse(clientId);
    }

    public static byte[] Encode(HelloWorldRequest request)
    {
        return new SocketMessageFrame(
            request.ClientId,
            RequestMessageId,
            Array.Empty<byte>()).Encode();
    }

    public static byte[] Encode(HelloWorldResponse response)
    {
        return new SocketMessageFrame(
            response.ClientId,
            ResponseMessageId,
            Encoding.UTF8.GetBytes(response.Message)).Encode();
    }

    public static bool Send(Socket socket, HelloWorldRequest request)
    {
        return SendAsync(socket, request).GetAwaiter().GetResult();
    }

    public static Task<bool> SendAsync(Socket socket, HelloWorldRequest request)
    {
        return SocketMessageFrame.SendAsync(socket, new SocketMessageFrame(
            request.ClientId,
            RequestMessageId,
            Array.Empty<byte>()));
    }

    public static bool Send(Socket socket, HelloWorldResponse response)
    {
        return SendAsync(socket, response).GetAwaiter().GetResult();
    }

    public static Task<bool> SendAsync(Socket socket, HelloWorldResponse response)
    {
        return SocketMessageFrame.SendAsync(socket, new SocketMessageFrame(
            response.ClientId,
            ResponseMessageId,
            Encoding.UTF8.GetBytes(response.Message)));
    }

    public static bool TryReceiveRequest(Socket socket, out HelloWorldRequest request)
    {
        (bool success, HelloWorldRequest receivedRequest) = TryReceiveRequestAsync(socket).GetAwaiter().GetResult();
        request = receivedRequest;
        return success;
    }

    public static async Task<(bool Success, HelloWorldRequest Request)> TryReceiveRequestAsync(Socket socket)
    {
        (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(socket);
        if (!success || !TryDecodeRequest(frame, out HelloWorldRequest request))
        {
            return (false, null);
        }

        return (true, request);
    }

    public static bool TryReceiveResponse(Socket socket, out HelloWorldResponse response)
    {
        (bool success, HelloWorldResponse receivedResponse) = TryReceiveResponseAsync(socket).GetAwaiter().GetResult();
        response = receivedResponse;
        return success;
    }

    public static async Task<(bool Success, HelloWorldResponse Response)> TryReceiveResponseAsync(Socket socket)
    {
        (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(socket);
        if (!success || !TryDecodeResponse(frame, out HelloWorldResponse response))
        {
            return (false, null);
        }

        return (true, response);
    }

    public static bool TryDecodeRequest(byte[] bytes, out HelloWorldRequest request)
    {
        request = null;
        if (!SocketMessageFrame.TryDecode(bytes, out SocketMessageFrame frame))
        {
            return false;
        }

        return TryDecodeRequest(frame, out request);
    }

    public static bool TryDecodeRequest(SocketMessageFrame frame, out HelloWorldRequest request)
    {
        request = null;
        if (frame.MessageId != RequestMessageId || frame.Payload.Length != 0)
        {
            return false;
        }

        request = CreateRequest(frame.ClientId);
        return true;
    }

    public static bool TryDecodeResponse(byte[] bytes, out HelloWorldResponse response)
    {
        response = null;
        if (!SocketMessageFrame.TryDecode(bytes, out SocketMessageFrame frame))
        {
            return false;
        }

        return TryDecodeResponse(frame, out response);
    }

    public static bool TryDecodeResponse(SocketMessageFrame frame, out HelloWorldResponse response)
    {
        response = null;
        if (frame.MessageId != ResponseMessageId || frame.Payload.Length == 0)
        {
            return false;
        }

        response = new HelloWorldResponse(frame.ClientId, Encoding.UTF8.GetString(frame.Payload));
        return true;
    }
}
