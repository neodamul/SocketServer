using System;
using System.Net.Sockets;
using System.Text;

namespace SocketCommon.Model;

public class HelloWorldRequest
{
    public HelloWorldRequest()
    {
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset CreatedAt { get; }
}

public class HelloWorldResponse
{
    public HelloWorldResponse(string message = HelloWorldProtocol.DefaultMessage)
    {
        this.Message = message;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    public string Message { get; }

    public DateTimeOffset CreatedAt { get; }
}

public static class HelloWorldProtocol
{
    public const string Name = "HELLOWORLD";
    public const int Version = 1;
    public const string DefaultMessage = "Hello, World!";

    private const string RequestMessage = "HELLOWORLD/1 REQUEST\n";
    private const string ResponsePrefix = "HELLOWORLD/1 RESPONSE ";
    private const int MaxMessageLength = 256;

    public static HelloWorldRequest CreateRequest()
    {
        return new HelloWorldRequest();
    }

    public static HelloWorldResponse CreateResponse()
    {
        return new HelloWorldResponse();
    }

    public static byte[] Encode(HelloWorldRequest request)
    {
        return Encoding.UTF8.GetBytes(RequestMessage);
    }

    public static byte[] Encode(HelloWorldResponse response)
    {
        return Encoding.UTF8.GetBytes(ResponsePrefix + response.Message + "\n");
    }

    public static bool Send(Socket socket, HelloWorldRequest request)
    {
        return Send(socket, Encode(request));
    }

    public static bool Send(Socket socket, HelloWorldResponse response)
    {
        return Send(socket, Encode(response));
    }

    public static bool TryReceiveRequest(Socket socket, out HelloWorldRequest request)
    {
        request = null;
        if (!TryReceiveLine(socket, out string text))
        {
            return false;
        }

        return TryDecodeRequest(text, out request);
    }

    public static bool TryReceiveResponse(Socket socket, out HelloWorldResponse response)
    {
        response = null;
        if (!TryReceiveLine(socket, out string text))
        {
            return false;
        }

        return TryDecodeResponse(text, out response);
    }

    public static bool TryDecodeRequest(byte[] bytes, out HelloWorldRequest request)
    {
        return TryDecodeRequest(Encoding.UTF8.GetString(bytes), out request);
    }

    public static bool TryDecodeRequest(string text, out HelloWorldRequest request)
    {
        request = null;
        if (text.Trim() != "HELLOWORLD/1 REQUEST")
        {
            return false;
        }

        request = CreateRequest();
        return true;
    }

    public static bool TryDecodeResponse(byte[] bytes, out HelloWorldResponse response)
    {
        return TryDecodeResponse(Encoding.UTF8.GetString(bytes), out response);
    }

    public static bool TryDecodeResponse(string text, out HelloWorldResponse response)
    {
        response = null;
        string normalized = text.Trim();

        if (!normalized.StartsWith(ResponsePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string message = normalized[ResponsePrefix.Length..];
        if (String.IsNullOrEmpty(message))
        {
            return false;
        }

        response = new HelloWorldResponse(message);
        return true;
    }

    private static bool Send(Socket socket, byte[] bytes)
    {
        try
        {
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

    private static bool TryReceiveLine(Socket socket, out string text)
    {
        text = null;
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
                    text = Encoding.UTF8.GetString(buffer, 0, offset + 1);
                    return true;
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
}
