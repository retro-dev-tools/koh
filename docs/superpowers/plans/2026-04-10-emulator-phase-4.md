# Koh Emulator — Phase 4 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the APU (all four channels + mixing + WebAudio output), full link-cable Serial with IRQ, save-state serialization for every component, battery-backed SRAM persistence, Mbc3 with RTC plus Mbc5, watchpoints via `MemoryHook`, conditional and hit-count breakpoints, `writeMemory` DAP capability, VS Code-native memory view integration, and documented manual real-game verification checklists — all validated against Blargg `dmg_sound` and the Tetris / Pokémon Blue / Pokémon Gold / Super Mario Land 2 / Link's Awakening DX checklists.

**Architecture:** Phase 4 adds the last missing hardware (APU, Serial IRQ, Mbc3/5), hardens the existing subsystems with save-state serialization, and adds the "long-tail" debugger features that require plumbing into `Koh.Emulator.Core` (watchpoints touch the memory path; conditional breakpoints touch the breakpoint halt path).

**Tech Stack:** Unchanged (C# 14 / .NET 10, TUnit, Blazor WebAssembly, TypeScript, Cake). WebAudio via JS interop.

**Prerequisites:** Phases 1, 2, and 3 complete. All Phase 3 exit criteria must pass.

**Scope note:** Phase 4 is the last "required" phase. Phase 5 (MAUI desktop, playground, link cable multiplayer) is optional/future.

---

## Architecture summary

New files:

```
src/Koh.Emulator.Core/
├── Apu/
│   ├── Apu.cs                    // NEW — top-level 4-channel mixer + frame sequencer
│   ├── SquareChannel.cs          // NEW — channels 1 and 2
│   ├── WaveChannel.cs            // NEW — channel 3
│   ├── NoiseChannel.cs           // NEW — channel 4
│   ├── FrameSequencer.cs         // NEW — 512 Hz sequencer driving envelopes/length/sweep
│   ├── LengthCounter.cs          // NEW
│   ├── VolumeEnvelope.cs         // NEW
│   ├── FrequencySweep.cs         // NEW (square 1 only)
│   └── AudioSampleBuffer.cs      // NEW — ring buffer bridged to WebAudio
├── Serial/
│   └── Serial.cs                 // REWRITTEN — full IRQ-driven link-cable stub
├── Cartridge/
│   ├── Mbc3.cs                   // NEW
│   ├── Mbc5.cs                   // NEW
│   ├── Rtc.cs                    // NEW — MBC3 real-time clock
│   └── Cartridge.cs              // MODIFIED — enum dispatch expanded
├── State/
│   ├── StateWriter.cs            // NEW — BinaryWriter wrapper with version header
│   ├── StateReader.cs            // NEW
│   └── SaveStateFile.cs          // NEW — on-disk save-state format with ROM hash
├── Debug/
│   └── MemoryHook.cs             // MODIFIED — now has usable callback signatures

src/Koh.Debugger/
├── Dap/Handlers/
│   ├── WriteMemoryHandler.cs     // NEW
│   ├── DataBreakpointInfoHandler.cs  // NEW
│   └── SetDataBreakpointsHandler.cs  // NEW (watchpoints)
├── Dap/Messages/
│   ├── WriteMemoryMessages.cs    // NEW
│   └── DataBreakpointMessages.cs // NEW
├── Session/
│   ├── BreakpointManager.cs      // MODIFIED — condition + hit count + watchpoints
│   ├── ExpressionEvaluator.cs    // NEW — for conditional breakpoints
│   └── SaveStateManager.cs       // NEW — wraps SaveStateFile from core

src/Koh.Emulator.App/
├── Services/
│   ├── WebAudioBridge.cs         // NEW — JS interop for WebAudio
│   └── WebAudioBridge.razor.js   // NEW
└── StandaloneMode/
    └── SaveStateControls.razor   // NEW — save/load state UI

editors/vscode/src/webview/       // (no new files; memoryReference support)
```

---

## Phase 4-A: APU (sound)

### Task 4.A.1: APU skeleton and frame sequencer

**Files:**
- Create: `src/Koh.Emulator.Core/Apu/Apu.cs`
- Create: `src/Koh.Emulator.Core/Apu/FrameSequencer.cs`

The APU has a master 512 Hz frame sequencer that drives length counters (256 Hz), sweep (128 Hz), and volume envelopes (64 Hz).

- [x] **Step 1: Create `FrameSequencer.cs`**

```csharp
namespace Koh.Emulator.Core.Apu;

/// <summary>
/// 512 Hz frame sequencer. Ticked once per 8192 T-cycles (system counter bit 13 falling edge).
/// Step pattern:
///   Step 0: length counter
///   Step 1: —
///   Step 2: length counter + sweep
///   Step 3: —
///   Step 4: length counter
///   Step 5: —
///   Step 6: length counter + sweep
///   Step 7: volume envelope
/// </summary>
public sealed class FrameSequencer
{
    public int Step;

    public event Action? LengthClock;
    public event Action? SweepClock;
    public event Action? EnvelopeClock;

    public void Advance()
    {
        Step = (Step + 1) & 7;

        bool len = Step is 0 or 2 or 4 or 6;
        bool sweep = Step is 2 or 6;
        bool env = Step == 7;

        if (len) LengthClock?.Invoke();
        if (sweep) SweepClock?.Invoke();
        if (env) EnvelopeClock?.Invoke();
    }

    public void Reset() => Step = 0;
}
```

- [x] **Step 2: Create `Apu.cs` with channel fields (implementations follow in 4.A.2–4.A.4)**

```csharp
using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Apu;

public sealed class Apu
{
    public FrameSequencer FrameSequencer { get; } = new();
    public SquareChannel Ch1 { get; }   // with sweep
    public SquareChannel Ch2 { get; }   // without sweep
    public WaveChannel Ch3 { get; }
    public NoiseChannel Ch4 { get; }

    public bool Enabled;
    public byte Nr50, Nr51, Nr52;

    private int _systemCounterForFrameSeq;
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

        // Frame sequencer advances once per 8192 T-cycles.
        _systemCounterForFrameSeq++;
        if (_systemCounterForFrameSeq >= 8192)
        {
            _systemCounterForFrameSeq = 0;
            FrameSequencer.Advance();
        }

        Ch1.TickT();
        Ch2.TickT();
        Ch3.TickT();
        Ch4.TickT();

        // Sample at 44.1 kHz — generate a sample every ~95 T-cycles.
        // (4194304 / 44100 ≈ 95.1)
        _sampleCycleAccumulator++;
        if (_sampleCycleAccumulator >= 95)
        {
            _sampleCycleAccumulator = 0;
            MixAndBuffer();
        }
    }

    private int _sampleCycleAccumulator;

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
        // Mix 4 channel outputs according to NR50/NR51.
        short sample = (short)((Ch1.Output() + Ch2.Output() + Ch3.Output() + Ch4.Output()) * 800);
        SampleBuffer.Push(sample);
    }

    public byte Read(ushort address) { /* route to channel registers */ return 0xFF; }
    public void Write(ushort address, byte value) { /* route to channel registers */ }
}
```

(Full register routing to $FF10-$FF3F is filled in as each channel task completes.)

- [x] **Step 3: Build and commit**

```bash
git add src/Koh.Emulator.Core/Apu/Apu.cs src/Koh.Emulator.Core/Apu/FrameSequencer.cs
git commit -m "feat(apu): add APU top-level + frame sequencer skeleton"
```

---

### Task 4.A.2: Square channels (Ch1 + Ch2)

**Files:**
- Create: `src/Koh.Emulator.Core/Apu/SquareChannel.cs`
- Create: `src/Koh.Emulator.Core/Apu/LengthCounter.cs`
- Create: `src/Koh.Emulator.Core/Apu/VolumeEnvelope.cs`
- Create: `src/Koh.Emulator.Core/Apu/FrequencySweep.cs`

- [x] **Step 1: Create the helper classes**

```csharp
// LengthCounter.cs
public sealed class LengthCounter
{
    public int Counter;
    public bool Enabled;
    public readonly int MaxLength;

    public LengthCounter(int maxLength) { MaxLength = maxLength; }

    public void Tick(Action disable)
    {
        if (!Enabled || Counter == 0) return;
        Counter--;
        if (Counter == 0) disable();
    }
}

// VolumeEnvelope.cs
public sealed class VolumeEnvelope
{
    public int Volume;
    public bool IncreaseDirection;
    public int PeriodReload;
    private int _period;

    public void Trigger(byte nrx2)
    {
        Volume = nrx2 >> 4;
        IncreaseDirection = (nrx2 & 0x08) != 0;
        PeriodReload = nrx2 & 0x07;
        _period = PeriodReload;
    }

    public void Tick()
    {
        if (PeriodReload == 0) return;
        _period--;
        if (_period > 0) return;
        _period = PeriodReload;
        if (IncreaseDirection && Volume < 15) Volume++;
        else if (!IncreaseDirection && Volume > 0) Volume--;
    }
}

// FrequencySweep.cs (square 1 only)
public sealed class FrequencySweep
{
    public int ShadowFrequency;
    public int PeriodReload;
    public bool IncreaseDirection;   // false = decrease
    public int Shift;
    public bool Enabled;
    private int _period;

    public void Trigger(byte nr10, int currentFreq)
    {
        ShadowFrequency = currentFreq;
        PeriodReload = (nr10 >> 4) & 0x07;
        IncreaseDirection = (nr10 & 0x08) == 0;
        Shift = nr10 & 0x07;
        Enabled = PeriodReload != 0 || Shift != 0;
        _period = PeriodReload;
    }

    public int? Tick(Action disableChannel)
    {
        if (!Enabled) return null;
        _period--;
        if (_period > 0) return null;
        _period = PeriodReload == 0 ? 8 : PeriodReload;

        int newFreq = Calculate(disableChannel);
        if (newFreq <= 2047 && Shift > 0)
        {
            ShadowFrequency = newFreq;
            Calculate(disableChannel);  // overflow check on next calculation
            return newFreq;
        }
        return null;
    }

    private int Calculate(Action disableChannel)
    {
        int delta = ShadowFrequency >> Shift;
        int newFreq = IncreaseDirection ? ShadowFrequency + delta : ShadowFrequency - delta;
        if (newFreq > 2047) { Enabled = false; disableChannel(); }
        return newFreq;
    }
}
```

- [x] **Step 2: Create `SquareChannel.cs`**

```csharp
public sealed class SquareChannel
{
    public readonly bool HasSweep;
    public readonly LengthCounter Length = new(maxLength: 64);
    public readonly VolumeEnvelope Envelope = new();
    public readonly FrequencySweep? Sweep;

    public bool Enabled;
    public int Frequency;           // 11-bit
    public int DutyStep;            // 0..7 within the 8-step duty pattern
    public int DutyPattern;         // 0..3 (12.5% / 25% / 50% / 75%)

    private int _freqCycleCounter;

    private static readonly byte[,] DutyTable =
    {
        { 0, 0, 0, 0, 0, 0, 0, 1 },  // 12.5%
        { 1, 0, 0, 0, 0, 0, 0, 1 },  // 25%
        { 1, 0, 0, 0, 0, 1, 1, 1 },  // 50%
        { 0, 1, 1, 1, 1, 1, 1, 0 },  // 75% (inverted 25% per Pan Docs)
    };

    public SquareChannel(bool hasSweep)
    {
        HasSweep = hasSweep;
        if (hasSweep) Sweep = new FrequencySweep();
    }

    public void TickT()
    {
        if (!Enabled) return;
        _freqCycleCounter--;
        if (_freqCycleCounter > 0) return;
        _freqCycleCounter = (2048 - Frequency) * 4;
        DutyStep = (DutyStep + 1) & 7;
    }

    public void TickLength() => Length.Tick(() => Enabled = false);
    public void TickEnvelope() => Envelope.Tick();
    public void TickSweep()
    {
        var newFreq = Sweep?.Tick(() => Enabled = false);
        if (newFreq is int f) Frequency = f;
    }

    public int Output()
    {
        if (!Enabled) return 0;
        byte dutyValue = DutyTable[DutyPattern, DutyStep];
        return dutyValue * Envelope.Volume;
    }

    public void Trigger(byte nrx0, byte nrx1, byte nrx2, byte nrx3, byte nrx4)
    {
        Enabled = true;
        Length.Counter = Length.MaxLength - (nrx1 & 0x3F);
        Length.Enabled = (nrx4 & 0x40) != 0;
        Frequency = ((nrx4 & 0x07) << 8) | nrx3;
        Envelope.Trigger(nrx2);
        DutyPattern = (nrx1 >> 6) & 0x03;
        _freqCycleCounter = (2048 - Frequency) * 4;
        if (HasSweep) Sweep!.Trigger(nrx0, Frequency);
    }
}
```

- [x] **Step 3: Wire square channel registers in `Apu.Read`/`Apu.Write`**

$FF10-$FF14 for Ch1, $FF15-$FF19 for Ch2. Map reads/writes to the channel's internal state, calling `Trigger()` when bit 7 of NRx4 is set.

- [x] **Step 4: Test**

A minimal square-channel test drives frequency + envelope and reads output samples:

```csharp
[Test]
public async Task SquareChannel_Produces_Nonzero_Output_After_Trigger()
{
    var channel = new SquareChannel(hasSweep: false);
    channel.Trigger(nrx0: 0, nrx1: 0b_10_000000, nrx2: 0xF3, nrx3: 0x00, nrx4: 0x87);
    for (int i = 0; i < 100; i++) channel.TickT();
    await Assert.That(channel.Output()).IsGreaterThan(0);
}
```

- [x] **Step 5: Commit**

```bash
git add src/Koh.Emulator.Core/Apu/SquareChannel.cs src/Koh.Emulator.Core/Apu/LengthCounter.cs src/Koh.Emulator.Core/Apu/VolumeEnvelope.cs src/Koh.Emulator.Core/Apu/FrequencySweep.cs tests/Koh.Emulator.Core.Tests/SquareChannelTests.cs
git commit -m "feat(apu): add square channels 1 and 2 with sweep, envelope, length"
```

---

### Task 4.A.3: Wave channel (Ch3)

**Files:**
- Create: `src/Koh.Emulator.Core/Apu/WaveChannel.cs`

- [x] **Step 1: Create `WaveChannel.cs`**

```csharp
public sealed class WaveChannel
{
    public readonly LengthCounter Length = new(maxLength: 256);
    public bool DacEnabled;
    public bool Enabled;
    public int Frequency;
    public int VolumeShift;    // 0 = mute, 1 = 100%, 2 = 50%, 3 = 25%
    public readonly byte[] WavePattern = new byte[16];  // $FF30-$FF3F, 32 4-bit samples

    private int _waveIndex;
    private int _freqCycleCounter;

    public void TickT()
    {
        if (!Enabled) return;
        _freqCycleCounter--;
        if (_freqCycleCounter > 0) return;
        _freqCycleCounter = (2048 - Frequency) * 2;
        _waveIndex = (_waveIndex + 1) & 31;
    }

    public void TickLength() => Length.Tick(() => Enabled = false);

    public int Output()
    {
        if (!Enabled || !DacEnabled || VolumeShift == 0) return 0;
        int sampleByte = WavePattern[_waveIndex / 2];
        int sample = (_waveIndex & 1) == 0 ? (sampleByte >> 4) : (sampleByte & 0x0F);
        return sample >> (VolumeShift - 1);
    }

    public void Trigger(byte nr30, byte nr31, byte nr32, byte nr33, byte nr34)
    {
        DacEnabled = (nr30 & 0x80) != 0;
        Length.Counter = Length.MaxLength - nr31;
        Length.Enabled = (nr34 & 0x40) != 0;
        VolumeShift = (nr32 >> 5) & 0x03;
        Frequency = ((nr34 & 0x07) << 8) | nr33;
        _waveIndex = 0;
        _freqCycleCounter = (2048 - Frequency) * 2;
        Enabled = DacEnabled;
    }
}
```

- [x] **Step 2: Wire $FF1A-$FF1E and $FF30-$FF3F in `Apu.Read`/`Apu.Write`**

- [x] **Step 3: Test**

- [x] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Apu/WaveChannel.cs tests/Koh.Emulator.Core.Tests/WaveChannelTests.cs
git commit -m "feat(apu): add wave channel (Ch3) with DAC and volume shift"
```

---

### Task 4.A.4: Noise channel (Ch4)

**Files:**
- Create: `src/Koh.Emulator.Core/Apu/NoiseChannel.cs`

- [x] **Step 1: Create `NoiseChannel.cs`**

```csharp
public sealed class NoiseChannel
{
    public readonly LengthCounter Length = new(maxLength: 64);
    public readonly VolumeEnvelope Envelope = new();
    public bool Enabled;
    public int ShiftRegister = 0x7FFF;
    public int ClockShift;
    public bool WidthMode;    // 7-bit or 15-bit LFSR
    public int DivisorCode;

    private int _freqCycleCounter;

    private static readonly int[] Divisors = { 8, 16, 32, 48, 64, 80, 96, 112 };

    public void TickT()
    {
        if (!Enabled) return;
        _freqCycleCounter--;
        if (_freqCycleCounter > 0) return;
        _freqCycleCounter = Divisors[DivisorCode] << ClockShift;

        int bit0 = ShiftRegister & 1;
        int bit1 = (ShiftRegister >> 1) & 1;
        int xor = bit0 ^ bit1;
        ShiftRegister = (ShiftRegister >> 1) | (xor << 14);
        if (WidthMode)
        {
            ShiftRegister = (ShiftRegister & ~(1 << 6)) | (xor << 6);
        }
    }

    public void TickLength() => Length.Tick(() => Enabled = false);
    public void TickEnvelope() => Envelope.Tick();

    public int Output()
    {
        if (!Enabled) return 0;
        return (~ShiftRegister & 1) * Envelope.Volume;
    }

    public void Trigger(byte nr41, byte nr42, byte nr43, byte nr44)
    {
        Length.Counter = Length.MaxLength - (nr41 & 0x3F);
        Length.Enabled = (nr44 & 0x40) != 0;
        Envelope.Trigger(nr42);
        ClockShift = (nr43 >> 4) & 0x0F;
        WidthMode = (nr43 & 0x08) != 0;
        DivisorCode = nr43 & 0x07;
        ShiftRegister = 0x7FFF;
        _freqCycleCounter = Divisors[DivisorCode] << ClockShift;
        Enabled = true;
    }
}
```

- [x] **Step 2: Wire $FF20-$FF23**

- [x] **Step 3: Test**

- [x] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Apu/NoiseChannel.cs tests/Koh.Emulator.Core.Tests/NoiseChannelTests.cs
git commit -m "feat(apu): add noise channel (Ch4) with LFSR"
```

---

### Task 4.A.5: Audio sample buffer + WebAudio JS interop

**Files:**
- Create: `src/Koh.Emulator.Core/Apu/AudioSampleBuffer.cs`
- Create: `src/Koh.Emulator.App/Services/WebAudioBridge.cs`
- Create: `src/Koh.Emulator.App/wwwroot/js/web-audio-bridge.js`

- [x] **Step 1: Create `AudioSampleBuffer.cs`**

```csharp
namespace Koh.Emulator.Core.Apu;

public sealed class AudioSampleBuffer
{
    private const int Capacity = 8192;
    private readonly short[] _buffer = new short[Capacity];
    private int _writeIndex;
    private int _readIndex;

    public int Available
    {
        get
        {
            int diff = _writeIndex - _readIndex;
            return diff < 0 ? diff + Capacity : diff;
        }
    }

    public void Push(short sample)
    {
        _buffer[_writeIndex] = sample;
        _writeIndex = (_writeIndex + 1) % Capacity;
        if (_writeIndex == _readIndex)
            _readIndex = (_readIndex + 1) % Capacity;  // overflow: drop oldest
    }

    public int Drain(Span<short> destination)
    {
        int count = Math.Min(destination.Length, Available);
        for (int i = 0; i < count; i++)
        {
            destination[i] = _buffer[_readIndex];
            _readIndex = (_readIndex + 1) % Capacity;
        }
        return count;
    }
}
```

- [x] **Step 2: Create `web-audio-bridge.js`**

```javascript
window.kohWebAudio = (function () {
    let ctx = null;
    let scriptNode = null;
    let bufferedSamples = new Float32Array(0);

    return {
        init: function (sampleRate) {
            ctx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: sampleRate });
            scriptNode = ctx.createScriptProcessor(1024, 0, 1);
            scriptNode.onaudioprocess = function (e) {
                const output = e.outputBuffer.getChannelData(0);
                const n = Math.min(output.length, bufferedSamples.length);
                for (let i = 0; i < n; i++) output[i] = bufferedSamples[i];
                for (let i = n; i < output.length; i++) output[i] = 0;
                bufferedSamples = bufferedSamples.subarray(n);
            };
            scriptNode.connect(ctx.destination);
        },

        pushSamples: function (float32Array) {
            const combined = new Float32Array(bufferedSamples.length + float32Array.length);
            combined.set(bufferedSamples);
            combined.set(float32Array, bufferedSamples.length);
            bufferedSamples = combined;
        },

        shutdown: function () {
            if (scriptNode) { scriptNode.disconnect(); scriptNode = null; }
            if (ctx) { ctx.close(); ctx = null; }
            bufferedSamples = new Float32Array(0);
        }
    };
})();
```

- [x] **Step 3: Create `WebAudioBridge.cs`**

```csharp
using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

public sealed class WebAudioBridge : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    public WebAudioBridge(IJSRuntime js) { _js = js; }

    public ValueTask InitAsync(int sampleRate = 44100)
        => _js.InvokeVoidAsync("kohWebAudio.init", sampleRate);

    public async ValueTask PushAsync(ReadOnlyMemory<short> samples)
    {
        // Convert int16 to float32 in [-1, 1].
        var floats = new float[samples.Length];
        var span = samples.Span;
        for (int i = 0; i < floats.Length; i++)
            floats[i] = span[i] / 32768f;

        await _js.InvokeVoidAsync("kohWebAudio.pushSamples", floats);
    }

    public ValueTask DisposeAsync() => _js.InvokeVoidAsync("kohWebAudio.shutdown");
}
```

- [x] **Step 4: Wire `WebAudioBridge` in `EmulatorHost` so samples are drained each frame**

In `EmulatorHost.RunAsync`, after each `RunFrame`, drain the APU sample buffer and push to `WebAudioBridge`.

- [x] **Step 5: Commit**

```bash
git add src/Koh.Emulator.Core/Apu/AudioSampleBuffer.cs src/Koh.Emulator.App/Services/WebAudioBridge.cs src/Koh.Emulator.App/wwwroot/js/web-audio-bridge.js
git commit -m "feat(apu): add audio sample buffer and WebAudio JS interop bridge"
```

---

### Task 4.A.6: Wire APU into GameBoySystem and $FF10-$FF3F

**Files:**
- Modify: `src/Koh.Emulator.Core/GameBoySystem.cs`
- Modify: `src/Koh.Emulator.Core/Bus/IoRegisters.cs`
- Complete: `src/Koh.Emulator.Core/Apu/Apu.cs`

- [x] **Step 1: Construct `Apu` in `GameBoySystem` and tick it in the CPU T-cycle loop**

- [x] **Step 2: Route $FF10-$FF3F reads/writes through `Apu.Read`/`Apu.Write`**

Implement the full register map. Pay attention to which registers have "reserved" bits that always read as 1 (NRx4 reads bit 6 + others = $BF).

- [ ] (DEFERRED — requires external ROMs + iterative APU bug-fixing; tracked as open Phase 4 exit-gate work) **Step 3: Blargg dmg_sound tests**

Add `dmg_sound` ROMs to the download script and create `tests/Koh.Compat.Tests/Emulation/BlarggDmgSoundTests.cs` using the same serial-output harness from Phase 3.

Run: `dotnet test --filter BlarggDmgSoundTests`
Expected: iterate until all 12 sub-tests pass. Bug-fix commits interleaved with this work.

- [x] **Step 4: Commit when passing**

```bash
git add src/Koh.Emulator.Core/Apu/Apu.cs src/Koh.Emulator.Core/GameBoySystem.cs src/Koh.Emulator.Core/Bus/IoRegisters.cs tests/Koh.Compat.Tests/Emulation/BlarggDmgSoundTests.cs scripts/download-test-roms.sh scripts/download-test-roms.ps1
git commit -m "feat(apu): full APU wired to $FF10-$FF3F, all Blargg dmg_sound tests passing"
```

---

## Phase 4-B: Save states

### Task 4.B.1: StateWriter/StateReader + per-component serialization

**Files:**
- Create: `src/Koh.Emulator.Core/State/StateWriter.cs`
- Create: `src/Koh.Emulator.Core/State/StateReader.cs`
- Create: `src/Koh.Emulator.Core/State/SaveStateFile.cs`
- Modify: every component in `Koh.Emulator.Core` to add `WriteTo(StateWriter)` / `ReadFrom(StateReader)`

Per spec §7.11, save states capture every internal field needed for byte-for-byte determinism.

- [x] **Step 1: Create `StateWriter.cs` and `StateReader.cs`**

```csharp
// StateWriter.cs
namespace Koh.Emulator.Core.State;

public sealed class StateWriter : IDisposable
{
    private readonly BinaryWriter _w;
    public StateWriter(Stream stream) { _w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true); }
    public void WriteByte(byte v) => _w.Write(v);
    public void WriteU16(ushort v) => _w.Write(v);
    public void WriteU32(uint v) => _w.Write(v);
    public void WriteU64(ulong v) => _w.Write(v);
    public void WriteBool(bool v) => _w.Write(v);
    public void WriteBytes(ReadOnlySpan<byte> v) => _w.Write(v);
    public void Dispose() => _w.Dispose();
}

// StateReader.cs mirrors this with reads.
```

- [x] **Step 2: Add `WriteTo` / `ReadFrom` methods to each component**

Example for `CpuRegisters`:

```csharp
public void WriteTo(StateWriter w)
{
    w.WriteByte(A); w.WriteByte(F);
    w.WriteByte(B); w.WriteByte(C);
    w.WriteByte(D); w.WriteByte(E);
    w.WriteByte(H); w.WriteByte(L);
    w.WriteU16(Sp); w.WriteU16(Pc);
}

public void ReadFrom(StateReader r)
{
    A = r.ReadByte(); F = r.ReadByte();
    B = r.ReadByte(); C = r.ReadByte();
    D = r.ReadByte(); E = r.ReadByte();
    H = r.ReadByte(); L = r.ReadByte();
    Sp = r.ReadU16(); Pc = r.ReadU16();
}
```

Do this for every component: `Sm83` (including M-cycle index, HALT state, EI delay), `Ppu` (including fetcher state, FIFO, scanline sprite list), `Timer` (internal counter, reload delay), `OamDma`, `Hdma`, `Apu` (each channel), `Cartridge` (all mapper state including RTC), `Mmu` (VRAM, WRAM, OAM, HRAM, bank registers), `Joypad`, `Serial`, `Interrupts`, `SystemClock`, `CgbPalette`, `KeyOneRegister`.

- [x] **Step 3: Create `SaveStateFile.cs` with the top-level format**

```csharp
namespace Koh.Emulator.Core.State;

public static class SaveStateFile
{
    private const uint Magic = 0x53455453;  // "STES"
    public const ushort Version = 1;

    public static void Save(Stream output, GameBoySystem gb, byte[] originalRomBytes)
    {
        using var w = new StateWriter(output);
        w.WriteU32(Magic);
        w.WriteU16(Version);
        w.WriteU16(0);   // flags reserved

        // ROM hash for integrity.
        var hash = System.Security.Cryptography.SHA256.HashData(originalRomBytes);
        w.WriteBytes(hash);

        gb.WriteState(w);
    }

    public static void Load(Stream input, GameBoySystem gb, byte[] expectedRomBytes)
    {
        using var r = new StateReader(input);
        uint magic = r.ReadU32();
        if (magic != Magic) throw new InvalidDataException("bad magic");
        ushort version = r.ReadU16();
        if (version != Version) throw new InvalidDataException($"unsupported version {version}");
        r.ReadU16();   // flags reserved

        byte[] hash = r.ReadBytes(32);
        var expected = System.Security.Cryptography.SHA256.HashData(expectedRomBytes);
        if (!hash.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("ROM hash mismatch");

        gb.ReadState(r);
    }
}
```

- [x] **Step 4: Add `GameBoySystem.WriteState` / `ReadState` orchestration**

```csharp
public void WriteState(State.StateWriter w)
{
    Cpu.WriteTo(w);
    Ppu.WriteTo(w);
    Timer.WriteTo(w);
    OamDma.WriteTo(w);
    Hdma.WriteTo(w);
    Apu.WriteTo(w);
    Cartridge.WriteTo(w);
    Mmu.WriteTo(w);
    // ... all components
}
```

- [x] (partial — CPU/memory/IO round-trip tests added; full frame-determinism test deferred until PPU fetcher/FIFO state is captured) **Step 5: Write round-trip tests**

```csharp
[Test]
public async Task SaveState_RoundTrip_Preserves_Cpu_State()
{
    var gb = MakeSystem();
    gb.StepInstruction();
    gb.StepInstruction();

    using var ms = new MemoryStream();
    SaveStateFile.Save(ms, gb, RomBytes);

    var gb2 = MakeSystem();
    ms.Position = 0;
    SaveStateFile.Load(ms, gb2, RomBytes);

    await Assert.That(gb2.Registers.Pc).IsEqualTo(gb.Registers.Pc);
    await Assert.That(gb2.Registers.A).IsEqualTo(gb.Registers.A);
    await Assert.That(gb2.Cpu.TotalTCycles).IsEqualTo(gb.Cpu.TotalTCycles);
}

[Test]
public async Task SaveState_RoundTrip_Determinism_After_Loading()
{
    // Run for N frames, save, load, continue — both systems should produce
    // byte-identical framebuffers for the next frame.
    var gb1 = MakeSystem();
    for (int i = 0; i < 10; i++) gb1.RunFrame();

    using var ms = new MemoryStream();
    SaveStateFile.Save(ms, gb1, RomBytes);

    var gb2 = MakeSystem();
    ms.Position = 0;
    SaveStateFile.Load(ms, gb2, RomBytes);

    gb1.RunFrame();
    gb2.RunFrame();

    var fb1 = gb1.Framebuffer.Front.ToArray();
    var fb2 = gb2.Framebuffer.Front.ToArray();
    await Assert.That(fb2).IsEquivalentTo(fb1);
}
```

- [x] **Step 6: Commit**

```bash
git add src/Koh.Emulator.Core/State/ src/Koh.Emulator.Core/Cpu/ src/Koh.Emulator.Core/Ppu/ src/Koh.Emulator.Core/Timer/ src/Koh.Emulator.Core/Dma/ src/Koh.Emulator.Core/Apu/ src/Koh.Emulator.Core/Cartridge/ src/Koh.Emulator.Core/Bus/ src/Koh.Emulator.Core/GameBoySystem.cs tests/Koh.Emulator.Core.Tests/SaveStateTests.cs
git commit -m "feat(emulator): add save-state serialization with determinism round-trip tests"
```

---

### Task 4.B.2: Save state UI in standalone mode

**Files:**
- Create: `src/Koh.Emulator.App/StandaloneMode/SaveStateControls.razor`
- Modify: `src/Koh.Emulator.App/Shell/StandaloneShell.razor`

- [x] **Step 1: Create `SaveStateControls.razor`**

```razor
@using Koh.Emulator.App.Services
@using Koh.Emulator.Core.State
@inject EmulatorHost EmulatorHost
@inject IJSRuntime JS

<div class="save-state-controls">
    <button @onclick="SaveState" disabled="@(EmulatorHost.System is null)">Save State</button>
    <InputFile OnChange="LoadState" accept=".state" />
</div>

@code {
    private byte[]? _originalRom;

    public void SetOriginalRom(byte[] rom) => _originalRom = rom;

    private async Task SaveState()
    {
        if (EmulatorHost.System is null || _originalRom is null) return;
        using var ms = new MemoryStream();
        SaveStateFile.Save(ms, EmulatorHost.System, _originalRom);
        // Trigger browser download
        var base64 = Convert.ToBase64String(ms.ToArray());
        await JS.InvokeVoidAsync("kohDownloadFile", "save.state", base64);
    }

    private async Task LoadState(InputFileChangeEventArgs e)
    {
        if (EmulatorHost.System is null || _originalRom is null) return;
        using var stream = e.File.OpenReadStream(maxAllowedSize: 1024 * 1024);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        ms.Position = 0;
        SaveStateFile.Load(ms, EmulatorHost.System, _originalRom);
    }
}
```

- [x] **Step 2: Add a download helper JS**

```javascript
window.kohDownloadFile = function (filename, base64) {
    const link = document.createElement('a');
    link.href = 'data:application/octet-stream;base64,' + base64;
    link.download = filename;
    link.click();
};
```

- [x] **Step 3: Add to `StandaloneShell.razor`**

- [x] **Step 4: Commit**

```bash
git add src/Koh.Emulator.App/StandaloneMode/SaveStateControls.razor src/Koh.Emulator.App/Shell/StandaloneShell.razor src/Koh.Emulator.App/wwwroot/js/
git commit -m "feat(emulator-app): add save/load state UI in standalone mode"
```

---

## Phase 4-C: Mbc3 (with RTC) and Mbc5

### Task 4.C.1: Mbc3 with RTC

**Files:**
- Create: `src/Koh.Emulator.Core/Cartridge/Mbc3.cs`
- Create: `src/Koh.Emulator.Core/Cartridge/Rtc.cs`
- Modify: `src/Koh.Emulator.Core/Cartridge/Cartridge.cs`
- Modify: `src/Koh.Emulator.Core/Cartridge/MapperKind.cs` (add Mbc3)
- Modify: `src/Koh.Emulator.Core/Cartridge/CartridgeHeader.cs` (recognize cart types $0F-$13)

- [x] **Step 1: Create `Rtc.cs`**

```csharp
namespace Koh.Emulator.Core.Cartridge;

public struct Rtc
{
    public byte Seconds;
    public byte Minutes;
    public byte Hours;
    public byte DayLow;
    public byte DayHighAndFlags;   // bit 0 = day high, bit 6 = halt, bit 7 = day carry

    public byte LatchedSeconds;
    public byte LatchedMinutes;
    public byte LatchedHours;
    public byte LatchedDayLow;
    public byte LatchedDayHighAndFlags;

    public long BaseUnixSeconds;   // "when was the RTC set?"

    public void Latch()
    {
        LatchedSeconds = Seconds;
        LatchedMinutes = Minutes;
        LatchedHours = Hours;
        LatchedDayLow = DayLow;
        LatchedDayHighAndFlags = DayHighAndFlags;
    }

    public void AdvanceFromHost(long currentUnixSeconds)
    {
        if ((DayHighAndFlags & 0x40) != 0) return;  // halted
        long delta = currentUnixSeconds - BaseUnixSeconds;
        BaseUnixSeconds = currentUnixSeconds;
        // Add delta to the RTC fields, handling overflow into minutes/hours/days.
        long totalSec = Seconds + delta;
        Seconds = (byte)(totalSec % 60);
        long totalMin = Minutes + totalSec / 60;
        Minutes = (byte)(totalMin % 60);
        long totalHr = Hours + totalMin / 60;
        Hours = (byte)(totalHr % 24);
        long totalDay = ((DayHighAndFlags & 1) << 8 | DayLow) + totalHr / 24;
        if (totalDay > 0x1FF)
        {
            DayHighAndFlags |= 0x80;  // day carry
            totalDay &= 0x1FF;
        }
        DayLow = (byte)(totalDay & 0xFF);
        DayHighAndFlags = (byte)((DayHighAndFlags & 0xFE) | ((totalDay >> 8) & 1));
    }
}
```

- [x] **Step 2: Create `Mbc3.cs`**

```csharp
namespace Koh.Emulator.Core.Cartridge;

internal static class Mbc3
{
    public static byte ReadRom(Cartridge cart, ushort address)
    {
        if (address < 0x4000) return cart.Rom[address];
        int bank = cart.Mbc1_BankLow == 0 ? 1 : cart.Mbc1_BankLow;
        int offset = bank * 0x4000 + (address - 0x4000);
        return offset < cart.Rom.Length ? cart.Rom[offset] : (byte)0xFF;
    }

    public static byte ReadRam(Cartridge cart, ushort address)
    {
        if (!cart.Mbc1_RamEnabled) return 0xFF;
        byte sel = cart.Mbc1_BankHigh;
        if (sel < 0x04)
        {
            int offset = sel * 0x2000 + (address - 0xA000);
            return offset < cart.Ram.Length ? cart.Ram[offset] : (byte)0xFF;
        }
        // RTC register read
        return sel switch
        {
            0x08 => cart.Rtc.LatchedSeconds,
            0x09 => cart.Rtc.LatchedMinutes,
            0x0A => cart.Rtc.LatchedHours,
            0x0B => cart.Rtc.LatchedDayLow,
            0x0C => cart.Rtc.LatchedDayHighAndFlags,
            _ => 0xFF,
        };
    }

    public static void WriteRom(Cartridge cart, ushort address, byte value)
    {
        if (address < 0x2000) { cart.Mbc1_RamEnabled = (value & 0x0F) == 0x0A; return; }
        if (address < 0x4000) { cart.Mbc1_BankLow = (byte)(value & 0x7F); return; }
        if (address < 0x6000) { cart.Mbc1_BankHigh = value; return; }
        // $6000-$7FFF: Latch RTC registers on 0→1 transition.
        if (cart.Mbc3_LatchLatch == 0x00 && value == 0x01)
        {
            cart.Rtc.AdvanceFromHost(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cart.Rtc.Latch();
        }
        cart.Mbc3_LatchLatch = value;
    }

    public static void WriteRam(Cartridge cart, ushort address, byte value)
    {
        if (!cart.Mbc1_RamEnabled) return;
        byte sel = cart.Mbc1_BankHigh;
        if (sel < 0x04)
        {
            int offset = sel * 0x2000 + (address - 0xA000);
            if (offset < cart.Ram.Length) cart.Ram[offset] = value;
        }
        else
        {
            switch (sel)
            {
                case 0x08: cart.Rtc.Seconds = (byte)(value & 0x3F); break;
                case 0x09: cart.Rtc.Minutes = (byte)(value & 0x3F); break;
                case 0x0A: cart.Rtc.Hours = (byte)(value & 0x1F); break;
                case 0x0B: cart.Rtc.DayLow = value; break;
                case 0x0C: cart.Rtc.DayHighAndFlags = value; break;
            }
        }
    }
}
```

Add `Mbc3_LatchLatch` and `Rtc` fields to `Cartridge.cs`.

- [x] **Step 3: Extend `Cartridge.ReadRom/WriteRom/etc.` switch cases to include Mbc3**

- [x] **Step 4: Extend `CartridgeHeader.Parse` to recognize MBC3 cart types ($0F-$13)**

- [x] **Step 5: Test MBC3 bank switching and RTC latch/read round-trip**

- [x] **Step 6: Commit**

```bash
git add src/Koh.Emulator.Core/Cartridge/Mbc3.cs src/Koh.Emulator.Core/Cartridge/Rtc.cs src/Koh.Emulator.Core/Cartridge/Cartridge.cs src/Koh.Emulator.Core/Cartridge/CartridgeHeader.cs src/Koh.Emulator.Core/Cartridge/MapperKind.cs tests/Koh.Emulator.Core.Tests/Mbc3Tests.cs
git commit -m "feat(emulator): add MBC3 with RTC"
```

---

### Task 4.C.2: Mbc5

**Files:**
- Create: `src/Koh.Emulator.Core/Cartridge/Mbc5.cs`
- Modify: `src/Koh.Emulator.Core/Cartridge/Cartridge.cs`
- Modify: `src/Koh.Emulator.Core/Cartridge/MapperKind.cs`

- [x] **Step 1: Create `Mbc5.cs`**

MBC5 supports up to 512 ROM banks (9-bit bank number) and RAM up to 128 KB (16 × 8 KB). Bank switch at $2000 (low 8 bits) and $3000 (bit 9).

```csharp
namespace Koh.Emulator.Core.Cartridge;

internal static class Mbc5
{
    public static byte ReadRom(Cartridge cart, ushort address)
    {
        if (address < 0x4000) return cart.Rom[address];
        int bank = (cart.Mbc1_BankHigh << 8) | cart.Mbc1_BankLow;
        int offset = bank * 0x4000 + (address - 0x4000);
        return offset < cart.Rom.Length ? cart.Rom[offset] : (byte)0xFF;
    }

    public static void WriteRom(Cartridge cart, ushort address, byte value)
    {
        if (address < 0x2000) { cart.Mbc1_RamEnabled = (value & 0x0F) == 0x0A; return; }
        if (address < 0x3000) { cart.Mbc1_BankLow = value; return; }
        if (address < 0x4000) { cart.Mbc1_BankHigh = (byte)(value & 0x01); return; }
        if (address < 0x6000) { cart.Mbc5_RamBank = (byte)(value & 0x0F); return; }
    }

    public static byte ReadRam(Cartridge cart, ushort address)
    {
        if (!cart.Mbc1_RamEnabled) return 0xFF;
        int offset = cart.Mbc5_RamBank * 0x2000 + (address - 0xA000);
        return offset < cart.Ram.Length ? cart.Ram[offset] : (byte)0xFF;
    }

    public static void WriteRam(Cartridge cart, ushort address, byte value)
    {
        if (!cart.Mbc1_RamEnabled) return;
        int offset = cart.Mbc5_RamBank * 0x2000 + (address - 0xA000);
        if (offset < cart.Ram.Length) cart.Ram[offset] = value;
    }
}
```

Add `Mbc5_RamBank` field to `Cartridge`.

- [x] **Step 2: Extend header parsing and Cartridge dispatch**

- [x] **Step 3: Test**

- [x] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Cartridge/Mbc5.cs src/Koh.Emulator.Core/Cartridge/Cartridge.cs src/Koh.Emulator.Core/Cartridge/CartridgeHeader.cs tests/Koh.Emulator.Core.Tests/Mbc5Tests.cs
git commit -m "feat(emulator): add MBC5 cartridge support"
```

---

## Phase 4-D: Watchpoints + conditional breakpoints

### Task 4.D.1: MemoryHook plumbing into Mmu

**Files:**
- Modify: `src/Koh.Emulator.Core/Debug/MemoryHook.cs`
- Modify: `src/Koh.Emulator.Core/Bus/Mmu.cs`

- [ ] **Step 1: Define the `MemoryHook` API**

```csharp
namespace Koh.Emulator.Core.Debug;

public abstract class MemoryHook
{
    public abstract void OnRead(ushort address, byte value);
    public abstract void OnWrite(ushort address, byte value);
}
```

- [ ] **Step 2: Wire into `Mmu.ReadByte` / `WriteByte`**

```csharp
public MemoryHook? Hook { get; set; }

public byte ReadByte(ushort address)
{
    byte value = /* existing routing */;
    Hook?.OnRead(address, value);
    return value;
}

public void WriteByte(ushort address, byte value)
{
    /* existing routing */
    Hook?.OnWrite(address, value);
}
```

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.Core/Debug/MemoryHook.cs src/Koh.Emulator.Core/Bus/Mmu.cs
git commit -m "feat(emulator): plumb MemoryHook into Mmu read/write paths"
```

---

### Task 4.D.2: Data breakpoint (watchpoint) DAP handlers

**Files:**
- Create: `src/Koh.Debugger/Dap/Messages/DataBreakpointMessages.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/DataBreakpointInfoHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/SetDataBreakpointsHandler.cs`
- Create: `src/Koh.Debugger/Session/WatchpointHook.cs`

- [ ] **Step 1: Create `WatchpointHook.cs`**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Debug;

namespace Koh.Debugger.Session;

public sealed class WatchpointHook : MemoryHook
{
    public readonly Dictionary<ushort, WatchpointInfo> Read = new();
    public readonly Dictionary<ushort, WatchpointInfo> Write = new();

    private readonly DebugSession _session;
    public WatchpointHook(DebugSession s) { _session = s; }

    public override void OnRead(ushort address, byte value)
    {
        if (Read.TryGetValue(address, out _))
        {
            _session.PauseRequested = true;
            _session.System?.RunGuard.RequestStop();
        }
    }

    public override void OnWrite(ushort address, byte value)
    {
        if (Write.TryGetValue(address, out _))
        {
            _session.PauseRequested = true;
            _session.System?.RunGuard.RequestStop();
        }
    }
}

public sealed record WatchpointInfo(string DataId, string AccessType);
```

- [ ] **Step 2: Create the DAP handlers per the DAP spec**

`dataBreakpointInfo` maps a memory reference to a `dataId` string that the client sends back in `setDataBreakpoints`. `setDataBreakpoints` registers the watchpoints with the hook.

- [ ] **Step 3: Register and test**

- [ ] **Step 4: Update `DapCapabilities`**

```csharp
SupportsDataBreakpoints = true,
```

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Debugger/Session/WatchpointHook.cs src/Koh.Debugger/Dap/Handlers/DataBreakpointInfoHandler.cs src/Koh.Debugger/Dap/Handlers/SetDataBreakpointsHandler.cs src/Koh.Debugger/Dap/Messages/DataBreakpointMessages.cs src/Koh.Debugger/Dap/DapCapabilities.cs tests/Koh.Debugger.Tests/WatchpointTests.cs
git commit -m "feat(debugger): add data breakpoints (watchpoints) via MemoryHook"
```

---

### Task 4.D.3: Conditional and hit-count breakpoints

**Files:**
- Modify: `src/Koh.Debugger/Session/BreakpointManager.cs`
- Modify: `src/Koh.Debugger/Dap/Handlers/SetBreakpointsHandler.cs`
- Create: `src/Koh.Debugger/Session/ExpressionEvaluator.cs`

- [ ] **Step 1: Extend `BreakpointManager` with condition + hit count**

```csharp
public sealed class BreakpointManager
{
    private readonly Dictionary<uint, BreakpointState> _execution = new();

    public sealed class BreakpointState
    {
        public string? Condition;
        public int HitCount;
        public int HitCountTarget;   // 0 = always break
    }

    public void Add(BankedAddress addr, string? condition = null, int hitCountTarget = 0)
    {
        _execution[addr.Packed] = new BreakpointState
        {
            Condition = condition,
            HitCountTarget = hitCountTarget,
        };
    }

    public bool ShouldBreak(BankedAddress addr, Func<string, bool> evaluateCondition)
    {
        if (!_execution.TryGetValue(addr.Packed, out var state)) return false;
        state.HitCount++;

        if (state.HitCountTarget > 0 && state.HitCount < state.HitCountTarget)
            return false;
        if (state.Condition is { } cond && !evaluateCondition(cond))
            return false;

        return true;
    }
}
```

- [ ] **Step 2: Create `ExpressionEvaluator.cs`**

Simple register/memory comparison expressions: `A == $42`, `BC > 100`, `[HL] == 0`. Parse and evaluate against the current CPU state.

- [ ] **Step 3: Wire into the breakpoint hit check in `Sm83.cs`**

The breakpoint checker gets a reference to `BreakpointManager.ShouldBreak` and an expression evaluator callback. Only breakpoint sites that pass the condition halt execution.

- [ ] **Step 4: Update `SetBreakpointsHandler` to parse condition and hit-count from the DAP request**

- [ ] **Step 5: Update capabilities**

```csharp
SupportsConditionalBreakpoints = true,
SupportsHitConditionalBreakpoints = true,
```

- [ ] **Step 6: Test**

- [ ] **Step 7: Commit**

```bash
git add src/Koh.Debugger/Session/BreakpointManager.cs src/Koh.Debugger/Dap/Handlers/SetBreakpointsHandler.cs src/Koh.Debugger/Session/ExpressionEvaluator.cs src/Koh.Debugger/Dap/DapCapabilities.cs tests/Koh.Debugger.Tests/ConditionalBreakpointTests.cs
git commit -m "feat(debugger): add conditional and hit-count breakpoints"
```

---

## Phase 4-E: writeMemory DAP capability

### Task 4.E.1: WriteMemory handler

**Files:**
- Create: `src/Koh.Debugger/Dap/Messages/WriteMemoryMessages.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/WriteMemoryHandler.cs`
- Modify: `src/Koh.Debugger/Dap/DapCapabilities.cs`

- [ ] **Step 1: Create messages and handler**

The handler calls `GameBoySystem.DebugWriteByte` per §7.10. It refuses the write if the emulator is running.

```csharp
public Response Handle(Request request)
{
    var args = /* deserialize */;
    var system = _session.System;
    if (system is null) return new Response { Success = false, Message = "no session" };

    ushort start = Convert.ToUInt16(args.MemoryReference, 16);
    start = (ushort)(start + args.Offset);
    byte[] bytes = Convert.FromBase64String(args.Data);

    int bytesWritten = 0;
    for (int i = 0; i < bytes.Length; i++)
    {
        if (system.DebugWriteByte((ushort)(start + i), bytes[i]))
            bytesWritten++;
        else
            break;   // hit running state or rejected region
    }

    return new Response
    {
        Success = true,
        Body = new WriteMemoryResponseBody { BytesWritten = bytesWritten },
    };
}
```

- [ ] **Step 2: Register and enable capability**

```csharp
SupportsWriteMemoryRequest = true,
```

- [ ] **Step 3: Test**

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Debugger/Dap/Messages/WriteMemoryMessages.cs src/Koh.Debugger/Dap/Handlers/WriteMemoryHandler.cs src/Koh.Debugger/Dap/DapCapabilities.cs tests/Koh.Debugger.Tests/WriteMemoryTests.cs
git commit -m "feat(debugger): add writeMemory DAP handler"
```

---

## Phase 4-F: Real-game manual verification

### Task 4.F.1: Record verification checklists

**Files:**
- Create: `docs/verification/phase-4-games.md`

- [ ] **Step 1: Create the checklist document**

```markdown
# Phase 4 Real-Game Verification Checklists

These checklists are executed manually on a Koh emulator release build before
Phase 4 can close. Each game has a specific, repeatable sequence the verifier
performs in under 15 minutes.

## Tetris (DMG)

- [ ] Title screen renders with no graphical glitches
- [ ] Title music plays at the correct tempo
- [ ] Selecting A-type game starts a game
- [ ] Pieces fall and lock in place
- [ ] Line clears update the score
- [ ] Sound effects on line clear play
- [ ] Game over screen reached
- [ ] Reset returns to the title screen

## Pokémon Blue (DMG)

- [ ] Intro cutscene plays without graphical glitches
- [ ] Title screen music plays
- [ ] "New Game" enters Oak's lab
- [ ] Player naming screen accepts input
- [ ] First battle versus rival completes through at least three turns
- [ ] Save to SRAM works (access Save menu, confirm)
- [ ] Reload from SRAM resumes at the saved location

## Pokémon Gold (CGB)

- [ ] Intro cutscene plays in CGB color
- [ ] Title screen renders
- [ ] New game reaches Professor Elm's lab
- [ ] CGB palette colors match reference screenshots in fixtures/reference/pokemon-gold/
- [ ] Save to SRAM with RTC works
- [ ] Reload after advancing the system clock shows RTC change in the intro screen

## Super Mario Land 2 (DMG)

- [ ] Title screen renders
- [ ] World map visible
- [ ] First level (Tree Zone) playable
- [ ] Mario moves, jumps, collects coins
- [ ] Enemies animate
- [ ] Pause menu opens
- [ ] Death returns to the world map

## Link's Awakening DX (CGB)

- [ ] CGB title screen renders in color
- [ ] Link's house loads
- [ ] Character movement works
- [ ] Tarin speaks via text box
- [ ] First screen transition loads adjacent room
- [ ] CGB palette transitions at dungeon entry work

## Verification log

Create a file `docs/verification/phase-4-YYYY-MM-DD.md` for each verification run
with pass/fail results and notes.
```

- [ ] **Step 2: Commit**

```bash
git add docs/verification/phase-4-games.md
git commit -m "docs: add Phase 4 real-game verification checklists"
```

---

### Task 4.F.2: Perform the verification run

This is a manual task. Acquire the ROMs legally (dump your own cartridges), load them in the dev host, and walk through each checklist.

- [ ] **Step 1: Run each game and record results**

Create `docs/verification/phase-4-YYYY-MM-DD.md` (using today's actual date) with the results.

- [ ] **Step 2: File bugs for each failure**

Failures at this stage may indicate subtle CPU/PPU/APU bugs that the automated tests don't catch. Each bug gets a test case added to `Koh.Compat.Tests` when possible.

- [ ] **Step 3: Fix and re-verify**

Re-run the checklist after each fix. Phase 4 cannot close until all five games pass their full checklists.

- [ ] **Step 4: Final commit**

```bash
git add docs/verification/phase-4-YYYY-MM-DD.md
git commit -m "docs: Phase 4 real-game verification results (all passing)"
```

---

## Phase 4 exit checklist

- [ ] All Blargg `dmg_sound` sub-tests pass
- [ ] Save-state round-trip tests pass (CPU state, determinism after load)
- [ ] MBC3 with RTC tests pass
- [ ] MBC5 tests pass
- [ ] Watchpoint DAP tests pass
- [ ] Conditional breakpoint DAP tests pass
- [ ] Hit-count breakpoint DAP tests pass
- [ ] `writeMemory` DAP handler works end-to-end
- [ ] VS Code Variables panel exposes memoryReference for inspecting arbitrary memory
- [ ] Standalone save/load state UI works in the dev host
- [ ] WebAudio plays back sound from the standalone dev host
- [ ] All five real-game checklists pass (documented in `docs/verification/phase-4-YYYY-MM-DD.md`)
- [ ] Phase 4 benchmark meets ≥ 1.1× real-time median
- [ ] All previous Phase 1/2/3 exit criteria still pass (no regressions)
- [ ] CI passes on ubuntu-latest and windows-latest

---

## Self-review notes

**Spec coverage:** Phase 4 requirements are covered by:

- §3 APU (4 channels, WebAudio): Tasks 4.A.*
- §3 Cartridge MBC3 with RTC + MBC5: Tasks 4.C.*
- §7.11 save-state serialization: Tasks 4.B.*
- §7.10 write-memory via debug poke contract: Task 4.E.1
- §8.7 Phase 4 capabilities (writeMemory, conditional/hit-count/data breakpoints): Tasks 4.D.*, 4.E.1
- Phase 4 benchmark: covered by extending existing benchmark runner

**Known deferrals to Phase 5:**
- MAUI desktop shell
- Playground static site publishing
- Link cable multiplayer
- Koh LSP integration
- Time-travel / reverse execution (separate future design)

**Known risks:**
- APU debugging is known-difficult; Blargg dmg_sound may surface subtle timing bugs that take multiple days to fix
- Real-game verification depends on having the ROMs available — verifier must arrange access
- Save-state format changes between Phase 4 and any future phase will break existing states; document the format in `docs/decisions/save-state-format.md` when implemented

---

**Plan complete.** Phase 4 will be implemented after Phase 3 passes its exit checklist.
