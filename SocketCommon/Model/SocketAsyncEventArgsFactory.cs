using System.Collections.Concurrent;
using System.Net.Sockets;

namespace SocketCommon.Model;

public static class SocketAsyncEventArgsFactory
{
    public const int InitialPoolSize = 1000;
    public const int GrowthSize = 100;

    private static readonly ConcurrentBag<SocketAsyncEventArgs> Pool = new();
    private static readonly object GrowLock = new();

    static SocketAsyncEventArgsFactory()
    {
        Grow(InitialPoolSize);
    }

    public static int AvailableCount => Pool.Count;

    public static SocketAsyncEventArgs Rent()
    {
        if (!Pool.TryTake(out SocketAsyncEventArgs args))
        {
            lock (GrowLock)
            {
                if (Pool.IsEmpty)
                {
                    Grow(GrowthSize);
                }
            }

            if (!Pool.TryTake(out args))
            {
                args = new SocketAsyncEventArgs();
            }
        }

        return args;
    }

    public static void Return(SocketAsyncEventArgs args)
    {
        if (args == null)
        {
            return;
        }

        args.AcceptSocket = null;
        args.UserToken = null;
        args.SetBuffer(null, 0, 0);
        Pool.Add(args);
    }

    private static void Grow(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Pool.Add(new SocketAsyncEventArgs());
        }
    }
}
