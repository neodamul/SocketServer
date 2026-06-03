using System.Collections.Concurrent;
using System;
using System.Net.Sockets;
using System.Threading;

namespace SocketCommon.Model;

public static class SocketAsyncEventArgsFactory
{
    public const int InitialPoolSize = 1000;
    public const int GrowthSize = 100;
    public const int BufferSize = SocketAsyncEventArgsTransport.BufferSize;

    private static readonly ConcurrentBag<SocketAsyncEventArgs> Pool = new();
    private static readonly object GrowLock = new();
    private static int totalCreatedCount;
    private static int rentedCount;
    private static int inUseCount;
    private static int highWatermarkInUseCount;
    private static int growthCount;
    private static int configuredGrowthSize = GrowthSize;
    private static int maxRetainedCount = 20000;

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

    public static int ConfiguredGrowthSize => Volatile.Read(ref configuredGrowthSize);

    public static int MaxRetainedCount => Volatile.Read(ref maxRetainedCount);

    public static int BufferSlabCount => SocketBufferPool.SlabCount;

    public static long BufferBytesAllocated => SocketBufferPool.TotalBytesAllocated;

    public static void Configure(int initialSize, int growthSize, int maxRetained)
    {
        Interlocked.Exchange(ref configuredGrowthSize, Math.Max(1, growthSize));
        Interlocked.Exchange(ref maxRetainedCount, Math.Max(InitialPoolSize, maxRetained));
        EnsureCapacity(Math.Max(InitialPoolSize, initialSize));
    }

    public static void EnsureCapacity(int targetTotalCreatedCount)
    {
        lock (GrowLock)
        {
            int missing = targetTotalCreatedCount - TotalCreatedCount;
            if (missing > 0)
            {
                Grow(missing);
            }
        }
    }

    public static SocketAsyncEventArgs Rent()
    {
        if (!Pool.TryTake(out SocketAsyncEventArgs args))
        {
            lock (GrowLock)
            {
                if (Pool.IsEmpty)
                {
                    Grow(ConfiguredGrowthSize);
                }
            }

            if (!Pool.TryTake(out args))
            {
                args = CreateArgs();
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
        RestoreMappedBuffer(args);
        Interlocked.Decrement(ref inUseCount);
        if (Pool.Count >= MaxRetainedCount)
        {
            ReturnMappedBufferLease(args);
            args.Dispose();
            return;
        }

        Pool.Add(args);
    }

    public static bool TryGetBufferSegment(SocketAsyncEventArgs args, out ArraySegment<byte> segment)
    {
        if (args?.UserToken is SocketAsyncEventArgsContext context)
        {
            segment = context.BufferLease.Segment;
            return true;
        }

        segment = default;
        return false;
    }

    internal static SocketBufferLease GetBufferLease(SocketAsyncEventArgs args)
    {
        if (args?.UserToken is SocketAsyncEventArgsContext context)
        {
            return context.BufferLease;
        }

        throw new InvalidOperationException("SocketAsyncEventArgs does not have a mapped buffer lease.");
    }

    private static void Grow(int count)
    {
        Interlocked.Increment(ref growthCount);
        for (int i = 0; i < count; i++)
        {
            Pool.Add(CreateArgs());
            Interlocked.Increment(ref totalCreatedCount);
        }
    }

    private static SocketAsyncEventArgs CreateArgs()
    {
        SocketAsyncEventArgs args = new();
        SocketBufferLease lease = SocketBufferPool.Rent(BufferSize);
        args.UserToken = new SocketAsyncEventArgsContext(lease);
        args.SetBuffer(lease.Buffer, lease.Offset, lease.Length);
        return args;
    }

    private static void RestoreMappedBuffer(SocketAsyncEventArgs args)
    {
        if (args.UserToken is not SocketAsyncEventArgsContext context)
        {
            args.SetBuffer(null, 0, 0);
            return;
        }

        SocketBufferLease lease = context.BufferLease;
        args.SetBuffer(lease.Buffer, lease.Offset, lease.Length);
    }

    private static void ReturnMappedBufferLease(SocketAsyncEventArgs args)
    {
        if (args.UserToken is SocketAsyncEventArgsContext context)
        {
            SocketBufferPool.Return(context.BufferLease);
            args.UserToken = null;
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

    private sealed class SocketAsyncEventArgsContext
    {
        public SocketAsyncEventArgsContext(SocketBufferLease bufferLease)
        {
            BufferLease = bufferLease;
        }

        public SocketBufferLease BufferLease { get; }
    }
}
