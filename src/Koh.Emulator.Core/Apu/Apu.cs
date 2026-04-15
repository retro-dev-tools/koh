namespace Koh.Emulator.Core.Apu;

public sealed class Apu
{
    public FrameSequencer FrameSequencer { get; } = new();
    public SquareChannel Ch1 { get; }
    public SquareChannel Ch2 { get; }
    public WaveChannel Ch3 { get; }
    public NoiseChannel Ch4 { get; }

    public bool Enabled;
    public byte Nr50, Nr51, Nr52;

    private int _frameSeqCounter;
    private int _sampleCycleAccumulator;

    public AudioSampleBuffer SampleBuffer { get; } = new();

    public Apu()
    {
        Ch1 = new SquareChannel(hasSweep: true);
        Ch2 = new SquareChannel(hasSweep: false);
        Ch3 = new WaveChannel();
        Ch4 = new NoiseChannel();

        FrameSequencer.LengthClock += OnLength;
        FrameSequencer.SweepClock += () => Ch1.TickSweep();
        FrameSequencer.EnvelopeClock += OnEnvelope;
    }

    public void TickT()
    {
        if (!Enabled) return;

        _frameSeqCounter++;
        if (_frameSeqCounter >= 8192)
        {
            _frameSeqCounter = 0;
            FrameSequencer.Advance();
        }

        Ch1.TickT();
        Ch2.TickT();
        Ch3.TickT();
        Ch4.TickT();

        _sampleCycleAccumulator++;
        if (_sampleCycleAccumulator >= 95)
        {
            _sampleCycleAccumulator = 0;
            MixAndBuffer();
        }
    }

    private void OnLength()
    {
        Ch1.TickLength();
        Ch2.TickLength();
        Ch3.TickLength();
        Ch4.TickLength();
    }

    private void OnEnvelope()
    {
        Ch1.TickEnvelope();
        Ch2.TickEnvelope();
        Ch4.TickEnvelope();
    }

    private void MixAndBuffer()
    {
        short sample = (short)((Ch1.Output() + Ch2.Output() + Ch3.Output() + Ch4.Output()) * 800);
        SampleBuffer.Push(sample);
    }

    public byte Read(ushort address) => 0xFF;    // NR register routing filled in by later tasks
    public void Write(ushort address, byte value) { }
}
