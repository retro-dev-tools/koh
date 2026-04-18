using Koh.Emulator.App.Services;

namespace Koh.Emulator.Core.Tests;

public class AudioRingTests
{
    [Test]
    public async Task Empty_Ring_Reports_Zero_Available()
    {
        var ring = new AudioRing(capacity: 16);
        await Assert.That(ring.Available).IsEqualTo(0);
    }

    [Test]
    public async Task Push_Then_Drain_Returns_Same_Samples_In_Order()
    {
        var ring = new AudioRing(capacity: 16);
        short[] input = { 1, 2, 3, 4, 5 };
        ring.Push(input);
        await Assert.That(ring.Available).IsEqualTo(5);

        var output = new short[5];
        int n = ring.Drain(output);
        await Assert.That(n).IsEqualTo(5);
        await Assert.That(output).IsEquivalentTo(input);
        await Assert.That(ring.Available).IsEqualTo(0);
    }

    [Test]
    public async Task Push_At_Capacity_Drops_Oldest_On_Overflow()
    {
        var ring = new AudioRing(capacity: 4);
        ring.Push(new short[] { 1, 2, 3, 4 });            // ring full
        ring.Push(new short[] { 5 });                      // 1 is overwritten

        var output = new short[4];
        int n = ring.Drain(output);
        await Assert.That(n).IsEqualTo(4);
        await Assert.That(output).IsEquivalentTo(new short[] { 2, 3, 4, 5 });
    }

    [Test]
    public async Task Concurrent_Producer_And_Consumer_Preserve_Order()
    {
        const int Capacity = 1024;
        const int Total = 1_000_000;
        var ring = new AudioRing(capacity: Capacity);
        var consumed = new List<short>(Total);
        var consumerDone = new ManualResetEventSlim();

        var producer = new Thread(() =>
        {
            for (int i = 0; i < Total; i++)
            {
                while (ring.Available >= Capacity - 1) Thread.Yield();
                ring.Push(new short[] { (short)(i & 0x7FFF) });
            }
        });

        var consumer = new Thread(() =>
        {
            var buf = new short[128];
            int got = 0;
            while (got < Total)
            {
                int n = ring.Drain(buf);
                for (int i = 0; i < n; i++) consumed.Add(buf[i]);
                got += n;
                if (n == 0) Thread.Yield();
            }
            consumerDone.Set();
        });

        producer.Start();
        consumer.Start();
        await Assert.That(consumerDone.Wait(TimeSpan.FromSeconds(10))).IsTrue();

        await Assert.That(consumed.Count).IsEqualTo(Total);
        for (int i = 0; i < Total; i++)
            if (consumed[i] != (short)(i & 0x7FFF))
                throw new Exception($"order broken at index {i}: got {consumed[i]}");
    }
}
