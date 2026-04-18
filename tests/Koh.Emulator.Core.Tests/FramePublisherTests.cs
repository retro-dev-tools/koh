using Koh.Emulator.App.Services;

namespace Koh.Emulator.Core.Tests;

public class FramePublisherTests
{
    private const int FrameBytes = 160 * 144 * 4;

    [Test]
    public async Task Initial_Acquire_Returns_Blank_Buffer()
    {
        var pub = new FramePublisher(FrameBytes);
        var front = pub.AcquireFront();
        await Assert.That(front.Length).IsEqualTo(FrameBytes);
        pub.ReleaseFront(front);
    }

    [Test]
    public async Task Publish_Then_Acquire_Returns_Same_Bytes()
    {
        var pub = new FramePublisher(FrameBytes);
        var back = pub.AcquireBack();
        for (int i = 0; i < 8; i++) back[i] = (byte)(0x10 + i);
        pub.PublishBack(back);

        var front = pub.AcquireFront();
        for (int i = 0; i < 8; i++)
            await Assert.That(front[i]).IsEqualTo((byte)(0x10 + i));
        pub.ReleaseFront(front);
    }

    [Test]
    public async Task Publish_Concurrent_With_Acquire_Never_Returns_Torn_Buffer()
    {
        var pub = new FramePublisher(FrameBytes);
        var stop = new ManualResetEventSlim();
        int producerFrames = 0;
        int consumerReads = 0;
        Exception? consumerError = null;

        var producer = new Thread(() =>
        {
            byte tag = 1;
            while (!stop.IsSet)
            {
                var back = pub.AcquireBack();
                for (int i = 0; i < back.Length; i++) back[i] = tag;
                pub.PublishBack(back);
                producerFrames++;
                unchecked { tag++; if (tag == 0) tag = 1; }
            }
        });

        var consumer = new Thread(() =>
        {
            try
            {
                while (!stop.IsSet)
                {
                    var front = pub.AcquireFront();
                    byte tag = front[0];
                    for (int i = 1; i < 128; i++)
                    {
                        if (front[i] != tag)
                            throw new Exception($"torn frame at byte {i}: {front[i]} vs {tag}");
                    }
                    pub.ReleaseFront(front);
                    consumerReads++;
                }
            }
            catch (Exception ex) { consumerError = ex; stop.Set(); }
        });

        producer.Start();
        consumer.Start();
        await Task.Delay(TimeSpan.FromSeconds(1));
        stop.Set();
        producer.Join();
        consumer.Join();

        if (consumerError is not null) throw consumerError;
        await Assert.That(producerFrames).IsGreaterThan(100);
        await Assert.That(consumerReads).IsGreaterThan(100);
    }
}
