using System;

namespace SocketCommon.Model;

public sealed class SocketBufferLease
{
    internal SocketBufferLease(byte[] buffer, int offset, int length)
    {
        Buffer = buffer;
        Offset = offset;
        Length = length;
    }

    public byte[] Buffer { get; }

    public int Offset { get; }

    public int Length { get; }

    public ArraySegment<byte> Segment => new(Buffer, Offset, Length);

    public Memory<byte> Memory => Buffer.AsMemory(Offset, Length);

    public ReadOnlyMemory<byte> ReadOnlyMemory => Buffer.AsMemory(Offset, Length);
}
