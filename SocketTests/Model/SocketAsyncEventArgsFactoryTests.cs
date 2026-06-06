using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using SocketCommon.Model;

namespace SocketTests.Model;

[TestClass]
public class SocketAsyncEventArgsFactoryTests
{
    [TestMethod]
    public void InitialPoolSizeTest()
    {
        Assert.AreEqual(1000, SocketAsyncEventArgsFactory.InitialPoolSize);
        Assert.AreEqual(100, SocketAsyncEventArgsFactory.GrowthSize);
        Assert.IsTrue(SocketAsyncEventArgsFactory.TotalCreatedCount >= SocketAsyncEventArgsFactory.InitialPoolSize);
        Assert.IsTrue(SocketAsyncEventArgsFactory.GrowthCount >= 1);
    }

    [TestMethod]
    public void RentAndReturnTest()
    {
        int initialInUse = SocketAsyncEventArgsFactory.InUseCount;
        int initialRented = SocketAsyncEventArgsFactory.RentedCount;
        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();

        Assert.IsNotNull(args);
        Assert.AreEqual(initialInUse + 1, SocketAsyncEventArgsFactory.InUseCount);
        Assert.AreEqual(initialRented + 1, SocketAsyncEventArgsFactory.RentedCount);
        Assert.IsTrue(SocketAsyncEventArgsFactory.HighWatermarkInUseCount >= SocketAsyncEventArgsFactory.InUseCount);

        SocketAsyncEventArgsFactory.Return(args);
        Assert.AreEqual(initialInUse, SocketAsyncEventArgsFactory.InUseCount);
    }

    [TestMethod]
    public void RentAssignsMappedBufferSegmentTest()
    {
        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();

        try
        {
            Assert.IsTrue(SocketAsyncEventArgsFactory.TryGetBufferSegment(args, out ArraySegment<byte> segment));
            Assert.IsNotNull(segment.Array);
            Assert.AreEqual(SocketAsyncEventArgsFactory.BufferSize, segment.Count);
            Assert.AreSame(segment.Array, args.Buffer);
            Assert.AreEqual(segment.Offset, args.Offset);
            Assert.AreEqual(segment.Count, args.Count);
            Assert.IsTrue(SocketAsyncEventArgsFactory.BufferSlabCount >= 1);
            Assert.IsTrue(SocketAsyncEventArgsFactory.BufferBytesAllocated >= SocketAsyncEventArgsFactory.BufferSize);
        }
        finally
        {
            SocketAsyncEventArgsFactory.Return(args);
        }
    }

    [TestMethod]
    public void ReturnRestoresMappedBufferAfterSendOverrideTest()
    {
        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();
        SocketAsyncEventArgsFactory.TryGetBufferSegment(args, out ArraySegment<byte> segment);
        byte[] overrideBuffer = new byte[16];

        args.SetBuffer(overrideBuffer, 0, overrideBuffer.Length);
        SocketAsyncEventArgsFactory.Return(args);

        SocketAsyncEventArgs rerented = SocketAsyncEventArgsFactory.Rent();
        try
        {
            Assert.IsTrue(SocketAsyncEventArgsFactory.TryGetBufferSegment(rerented, out ArraySegment<byte> restoredSegment));
            Assert.AreSame(restoredSegment.Array, rerented.Buffer);
            Assert.AreEqual(restoredSegment.Offset, rerented.Offset);
            Assert.AreEqual(restoredSegment.Count, rerented.Count);
            Assert.AreNotSame(overrideBuffer, rerented.Buffer);
            Assert.IsTrue(restoredSegment.Count >= segment.Count);
        }
        finally
        {
            SocketAsyncEventArgsFactory.Return(rerented);
        }
    }

    [TestMethod]
    public async Task ReceiveMappedAsyncReturnsMappedBufferReferenceTest()
    {
        using Socket listener = SocketFactory.CreateTcpSocket();
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using Socket client = SocketFactory.CreateTcpSocket();
        EndPoint endpoint = listener.LocalEndPoint ?? throw new InvalidOperationException("Listener endpoint was not assigned.");
        Task connectTask = client.ConnectAsync(endpoint);
        using Socket server = await listener.AcceptAsync();
        await connectTask;

        byte[] payload = Encoding.UTF8.GetBytes("mapped-buffer");
        await SocketAsyncEventArgsTransport.SendAsync(client, payload);

        using SocketMappedReceiveBuffer receiveBuffer =
            await SocketAsyncEventArgsTransport.ReceiveMappedAsync(server, SocketAsyncEventArgsTransport.BufferSize);

        Assert.IsNotNull(receiveBuffer);
        Assert.AreEqual(payload.Length, receiveBuffer.Count);
        CollectionAssert.AreEqual(
            payload,
            receiveBuffer.Segment.Array!.Skip(receiveBuffer.Segment.Offset).Take(receiveBuffer.Count).ToArray());
    }

    [TestMethod]
    public void ConfigurePoolSettingsTest()
    {
        int targetSize = SocketAsyncEventArgsFactory.TotalCreatedCount + 10;

        SocketAsyncEventArgsFactory.Configure(targetSize, 25, 30000);

        Assert.IsTrue(SocketAsyncEventArgsFactory.TotalCreatedCount >= targetSize);
        Assert.AreEqual(25, SocketAsyncEventArgsFactory.ConfiguredGrowthSize);
        Assert.AreEqual(30000, SocketAsyncEventArgsFactory.MaxRetainedCount);

        SocketAsyncEventArgsFactory.Configure(SocketAsyncEventArgsFactory.InitialPoolSize, SocketAsyncEventArgsFactory.GrowthSize, 20000);
    }

    [TestMethod]
    public void ReturnBeyondMaxRetainedDecrementsTotalCreatedCountTest()
    {
        int targetSize = Math.Max(
            SocketAsyncEventArgsFactory.TotalCreatedCount,
            SocketAsyncEventArgsFactory.InitialPoolSize) + 5;
        SocketAsyncEventArgsFactory.Configure(targetSize, 1, SocketAsyncEventArgsFactory.InitialPoolSize);

        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();
        int totalCreatedBeforeReturn = SocketAsyncEventArgsFactory.TotalCreatedCount;
        SocketAsyncEventArgsFactory.Return(args);

        Assert.AreEqual(totalCreatedBeforeReturn - 1, SocketAsyncEventArgsFactory.TotalCreatedCount);

        SocketAsyncEventArgsFactory.Configure(SocketAsyncEventArgsFactory.InitialPoolSize, SocketAsyncEventArgsFactory.GrowthSize, 20000);
    }
}
