namespace SocketCommon.Interface;

public interface IServer : IClient
{
    public bool Start();

    public bool End();

    public bool Bind();

    public bool Listen();
}
