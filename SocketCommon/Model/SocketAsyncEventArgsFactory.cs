using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace SocketCommon.Model;

public static class SocketAsyncEventArgsFactory
{
    public const int InitialPoolSize = 1000;
    public const int GrowthSize = 100;

    private static readonly ConcurrentBag<SocketAsyncEventArgs> Pool = new();
    private static readonly object GrowLock = new();
    private static int totalCreatedCount;
    private static int rentedCount;
    private static int inUseCount;
    private static int highWatermarkInUseCount;
    private static int growthCount;

    static SocketAsyncEventArgsFactory()
    {
        Grow(InitialPoolSize);
    }

    public static int AvailableCount => Pool.Count;

    public static int TotalCreatedCount => Volatile.Read(ref totalCreatedCount);

    public static int RentedCount => Volatile.Read(ref rentedCount);

    public static int InUseCount => Volatile.Read(ref inUseCount);

    public static int HighWatermarkInUseCount => Volatile.Read(ref highWatermarkInUseCount);

    public static int GrowthCount => Volatile.Read(ref growthCount);

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
                Interlocked.Increment(ref totalCreatedCount);
            }
        }

        Interlocked.Increment(ref rentedCount);
        int currentInUse = Interlocked.Increment(ref inUseCount);
        UpdateHighWatermark(currentInUse);
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
        Interlocked.Decrement(ref inUseCount);
        Pool.Add(args);
    }

    private static void Grow(int count)
    {
        Interlocked.Increment(ref growthCount);
        for (int i = 0; i < count; i++)
        {
            Pool.Add(new SocketAsyncEventArgs());
            Interlocked.Increment(ref totalCreatedCount);
        }
    }

    private static void UpdateHighWatermark(int currentInUse)
    {
        while (true)
        {
            int currentHigh = Volatile.Read(ref highWatermarkInUseCount);
            if (currentInUse <= currentHigh)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref highWatermarkInUseCount, currentInUse, currentHigh) == currentHigh)
            {
                return;
            }
        }
    }
}
