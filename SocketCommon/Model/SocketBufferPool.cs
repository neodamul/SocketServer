using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SocketCommon.Model;

public static class SocketBufferPool
{
    public const int DefaultSegmentsPerSlab = 1000;

    private static readonly object SyncRoot = new();
    private static readonly ConcurrentStack<SocketBufferLease> FreeSegments = new();
    private static readonly List<byte[]> Slabs = new();
    private static int nextOffset;
    private static long totalBytesAllocated;

    public static int SlabCount
    {
        get
        {
            lock (SyncRoot)
            {
                return Slabs.Count;
            }
        }
    }

    public static long TotalBytesAllocated => Interlocked.Read(ref totalBytesAllocated);

    public static int AvailableSegmentCount => FreeSegments.Count;

    public static SocketBufferLease Rent(int segmentLength)
    {
        if (segmentLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentLength), "Segment length must be greater than zero.");
        }

        if (FreeSegments.TryPop(out SocketBufferLease lease))
        {
            return lease;
        }

        lock (SyncRoot)
        {
            byte[] slab = GetWritableSlab(segmentLength);
            lease = new SocketBufferLease(slab, nextOffset, segmentLength);
            nextOffset += segmentLength;
            return lease;
        }
    }

    public static void Return(SocketBufferLease lease)
    {
        if (lease == null)
        {
            return;
        }

        FreeSegments.Push(lease);
    }

    private static byte[] GetWritableSlab(int segmentLength)
    {
        if (Slabs.Count == 0 || nextOffset + segmentLength > Slabs[^1].Length)
        {
            int slabLength = checked(segmentLength * DefaultSegmentsPerSlab);
            byte[] slab = new byte[Math.Max(segmentLength, slabLength)];
            Slabs.Add(slab);
            nextOffset = 0;
            Interlocked.Add(ref totalBytesAllocated, slab.Length);
        }

        return Slabs[^1];
    }
}
