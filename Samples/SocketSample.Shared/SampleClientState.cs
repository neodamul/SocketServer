namespace SocketSample.Shared;

public sealed class SampleClientState
{
    public bool IsConnected { get; init; }

    public bool IsRegistered { get; init; }

    public int ClientId { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; }

    public bool UseControlServer { get; init; }

    public string ConnectedServer { get; init; } = "";

    public string Status { get; init; } = "";

    public string LastReceivedMessage { get; init; } = "";

    public string LastError { get; init; } = "";
}
