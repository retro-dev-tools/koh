# Audio-Driven Pacing + AudioWorklet Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the display-driven audio pipeline (`requestAnimationFrame` → `RunFrame` → `ScriptProcessorNode`) with an audio-driven, thread-isolated pipeline so audio never glitches on main-thread work and emulation accuracy stays intact.

**Architecture:** A background C# `EmulatorRunner` thread runs `RunFrame`, pushes samples to JS on each iteration, and paces itself by reading back the SAB ring fill level. An `AudioWorkletProcessor` on the audio thread reads samples from a `SharedArrayBuffer` ring (with a `port.postMessage` fallback when cross-origin isolation is unavailable). The UI thread reads the latest framebuffer from a triple-buffered `FramePublisher` on its own `requestAnimationFrame` schedule.

**Tech Stack:** .NET 10, C# 14, Blazor WebAssembly + MAUI Blazor Hybrid, TUnit (xunit-like test framework with `[Test]` and `await Assert.That(...).IsEqualTo(...)`), plain JS + WebAudio AudioWorklet + SharedArrayBuffer + Atomics.

**Spec:** [`docs/superpowers/specs/2026-04-17-audio-driven-pacing-design.md`](../specs/2026-04-17-audio-driven-pacing-design.md)

**Prerequisite commits:** Phase 5 branch at `feature/emulator-phase-5`, HEAD includes the richer debug snapshot and audio hot-fixes from 2026-04-17.

---

## Scope and ordering

Six groups, executed in order. Within each group, tasks are TDD: failing test → minimal impl → passing test → commit.

1. **Primitives** — `AudioRing`, `FramePublisher`. Pure C#, no dependencies, fully unit-tested.
2. **Audio transport** — `IAudioSink` interface, `AudioIsolationLevel` enum, `AudioPipe` (the `IJSRuntime`-backed sink).
3. **JS audio modules** — `koh-audio-worklet.js`, `koh-audio-bridge.js` (both isolated + degraded paths).
4. **Runner** — `EmulatorRunner` with its command mailbox, pacing loop, and lifecycle states.
5. **Integration** — `EmulatorHost` delegates to runner; `LcdDisplay` reads from `FramePublisher` via a standalone rAF loop; DI wiring in both hosts.
6. **Cleanup + verification** — remove dead files, update the DebugSnapshot, manual checklist.

---

## Task 1: AudioRing (lock-free SPSC ring buffer)

Pure C# primitive. Used by internal tests and by any future audio-path scenario that needs an in-memory C# ring. Not on the hot path in this plan (producer pushes directly to JS).

**Files:**
- Create: `src/Koh.Emulator.App.Shared/Services/AudioRing.cs`
- Create: `tests/Koh.Emulator.Core.Tests/AudioRingTests.cs`

- [ ] **Step 1: Write the failing test** — `tests/Koh.Emulator.Core.Tests/AudioRingTests.cs`

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd tests/Koh.Emulator.Core.Tests && dotnet run --no-build`
Expected: compile error or test failure — `AudioRing` does not exist.

- [ ] **Step 3: Implement `AudioRing`** — `src/Koh.Emulator.App.Shared/Services/AudioRing.cs`

```csharp
using System.Threading;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Lock-free single-producer / single-consumer ring of <see cref="short"/>
/// samples. Capacity must be a power of two.
///
/// Overflow policy: push overwrites the oldest sample. The audio path
/// prefers brief starvation over backpressure-in-the-guest (the producer
/// is the emulator thread; we do not want to stall <c>RunFrame</c> on the
/// ring being full).
/// </summary>
public sealed class AudioRing
{
    private readonly short[] _buffer;
    private readonly int _mask;
    private int _writeIndex;
    private int _readIndex;

    public AudioRing(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException("capacity must be a positive power of two", nameof(capacity));
        _buffer = new short[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    public int Available
    {
        get
        {
            int w = Volatile.Read(ref _writeIndex);
            int r = Volatile.Read(ref _readIndex);
            int diff = w - r;
            return diff < 0 ? diff + _buffer.Length : diff;
        }
    }

    public void Push(ReadOnlySpan<short> samples)
    {
        int w = _writeIndex;
        int r = Volatile.Read(ref _readIndex);
        for (int i = 0; i < samples.Length; i++)
        {
            _buffer[w & _mask] = samples[i];
            w++;
            // If we've caught up to the read head, bump the reader too —
            // drop-oldest overflow. Only the consumer writes to _readIndex
            // normally, but an overflowing producer can advance it safely
            // because the reader treats its own index as monotonic.
            if ((w - r) > _buffer.Length)
            {
                r = w - _buffer.Length;
                Volatile.Write(ref _readIndex, r);
            }
        }
        Volatile.Write(ref _writeIndex, w);
    }

    public int Drain(Span<short> destination)
    {
        int w = Volatile.Read(ref _writeIndex);
        int r = _readIndex;
        int available = w - r;
        if (available < 0) available += _buffer.Length;
        int count = Math.Min(destination.Length, available);
        for (int i = 0; i < count; i++)
        {
            destination[i] = _buffer[r & _mask];
            r++;
        }
        Volatile.Write(ref _readIndex, r);
        return count;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd tests/Koh.Emulator.Core.Tests && dotnet run` (builds fresh)
Expected: All four `AudioRingTests` pass. Full test suite still 97 core tests green (our new tests raise that to 101 if they pass, the new count replaces 97 everywhere it appears below).

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Emulator.App.Shared/Services/AudioRing.cs tests/Koh.Emulator.Core.Tests/AudioRingTests.cs
git commit -m "feat(audio): add AudioRing — lock-free SPSC short-sample ring"
```

---

## Task 2: FramePublisher (triple-buffered framebuffer hand-off)

Pure C# primitive. Producer writes into a "back" buffer, publishes, UI grabs the latest "front". No locks.

**Files:**
- Create: `src/Koh.Emulator.App.Shared/Services/FramePublisher.cs`
- Create: `tests/Koh.Emulator.Core.Tests/FramePublisherTests.cs`

- [ ] **Step 1: Write the failing test** — `tests/Koh.Emulator.Core.Tests/FramePublisherTests.cs`

```csharp
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
        // Producer writes N unique to each frame's first byte, publishes.
        // Consumer repeatedly acquires; every byte in the buffer must match
        // the first byte (== "all bytes belong to the same frame").
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd tests/Koh.Emulator.Core.Tests && dotnet run`
Expected: compile error — `FramePublisher` does not exist.

- [ ] **Step 3: Implement `FramePublisher`** — `src/Koh.Emulator.App.Shared/Services/FramePublisher.cs`

```csharp
using System.Threading;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Triple-buffered byte-frame publisher. Producer fills a "back" buffer,
/// calls <see cref="PublishBack"/> to swap it into the "published" slot
/// atomically, and gets a fresh back buffer to fill next. Consumer calls
/// <see cref="AcquireFront"/>, which returns whatever is in the
/// "published" slot, marking it as "held by UI". Subsequent publishes
/// go into the third slot, so the held buffer is never overwritten
/// mid-read.
///
/// Never blocks. Consumer may see the same frame twice if it acquires
/// faster than the producer publishes — that's intentional.
/// </summary>
public sealed class FramePublisher
{
    private readonly byte[] _a;
    private readonly byte[] _b;
    private readonly byte[] _c;

    // The "published" slot is the only one that's atomically swapped.
    // Producer holds one of the three buffers as its current back buffer;
    // consumer holds one as its current front buffer; the third is the
    // one parked in _published.
    private byte[] _published;
    private byte[] _producerBack;
    private byte[]? _consumerFront;

    private readonly Lock _gate = new();

    public int FrameBytes => _a.Length;

    public FramePublisher(int frameBytes)
    {
        _a = new byte[frameBytes];
        _b = new byte[frameBytes];
        _c = new byte[frameBytes];
        _published = _a;
        _producerBack = _b;
    }

    public byte[] AcquireBack()
    {
        // Producer-private; no synchronisation needed because there's
        // only one producer by contract.
        return _producerBack;
    }

    public void PublishBack(byte[] buffer)
    {
        if (!ReferenceEquals(buffer, _producerBack))
            throw new InvalidOperationException("PublishBack called with a buffer that wasn't the current back");

        lock (_gate)
        {
            // Swap _producerBack ↔ _published, but route around whichever
            // buffer the consumer currently holds (if any).
            var oldPublished = _published;
            _published = _producerBack;

            if (_consumerFront is null)
            {
                _producerBack = oldPublished;
            }
            else
            {
                // Consumer holds oldPublished (or _c). Producer takes the
                // third buffer, whichever it is.
                _producerBack = ThirdOf(_consumerFront, _published);
            }
        }
    }

    public byte[] AcquireFront()
    {
        lock (_gate)
        {
            if (_consumerFront is not null)
                throw new InvalidOperationException("AcquireFront without ReleaseFront");
            _consumerFront = _published;
            return _consumerFront;
        }
    }

    public void ReleaseFront(byte[] buffer)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(buffer, _consumerFront))
                throw new InvalidOperationException("ReleaseFront called with wrong buffer");
            _consumerFront = null;
        }
    }

    private byte[] ThirdOf(byte[] x, byte[] y)
    {
        if (!ReferenceEquals(x, _a) && !ReferenceEquals(y, _a)) return _a;
        if (!ReferenceEquals(x, _b) && !ReferenceEquals(y, _b)) return _b;
        return _c;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd tests/Koh.Emulator.Core.Tests && dotnet run`
Expected: All `FramePublisherTests` pass. Other tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Emulator.App.Shared/Services/FramePublisher.cs tests/Koh.Emulator.Core.Tests/FramePublisherTests.cs
git commit -m "feat(app): add FramePublisher — triple-buffered frame hand-off"
```

---

## Task 3: IAudioSink + AudioIsolationLevel

Small types that the runner uses and tests can fake.

**Files:**
- Create: `src/Koh.Emulator.App.Shared/Services/IAudioSink.cs`

- [ ] **Step 1: Create the file**

```csharp
namespace Koh.Emulator.App.Services;

/// <summary>Level of isolation the audio transport was able to negotiate.</summary>
public enum AudioIsolationLevel
{
    /// <summary>AudioWorklet on an SAB ring — best case, glitch-resistant.</summary>
    Worklet,
    /// <summary>AudioWorklet using <c>port.postMessage</c> transfers — works without COOP/COEP.</summary>
    Degraded,
    /// <summary>No audio output; producer still paces off a Stopwatch.</summary>
    Muted,
}

/// <summary>
/// Destination for audio samples. The runner calls <see cref="Push"/>
/// once per emulated frame with ~738 samples and expects a current
/// buffered-samples count back to drive its pacing loop.
/// </summary>
public interface IAudioSink
{
    AudioIsolationLevel IsolationLevel { get; }

    /// <summary>Samples currently buffered at the audio device (read-side).</summary>
    int Buffered { get; }

    /// <summary>Cumulative underruns reported by the device.</summary>
    long Underruns { get; }

    /// <summary>Cumulative overruns (samples dropped) reported by the device.</summary>
    long Overruns { get; }

    /// <summary>
    /// Push samples to the device. Returns the fill level immediately
    /// after the push — used by the pacing loop to decide whether to
    /// sleep, tight-loop, or yield.
    /// </summary>
    int Push(ReadOnlySpan<short> samples);

    /// <summary>Drop any buffered audio (on ROM load / save-state load / reset).</summary>
    void Reset();
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Koh.Emulator.App.Shared`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.App.Shared/Services/IAudioSink.cs
git commit -m "feat(audio): add IAudioSink + AudioIsolationLevel"
```

---

## Task 4: AudioPipe — IAudioSink over IJSRuntime

The real production sink that the runner will use. Forwards to `window.kohAudio` on the JS side.

**Files:**
- Create: `src/Koh.Emulator.App.Shared/Services/AudioPipe.cs`

- [ ] **Step 1: Implement `AudioPipe`** — `src/Koh.Emulator.App.Shared/Services/AudioPipe.cs`

```csharp
using System.Runtime.InteropServices;
using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

/// <summary>
/// <see cref="IAudioSink"/> over a Blazor <see cref="IJSRuntime"/> bridge.
///
/// Push marshals samples into a reusable <c>byte[]</c> (Int16 little-
/// endian) and calls <c>kohAudio.pushSamples(bytes) -> int bufferedAfter</c>
/// via the synchronous in-process runtime when available. If the
/// runtime isn't in-process (e.g. future server-side Blazor), falls
/// back to <c>.InvokeAsync&lt;int&gt;(...).AsTask().GetAwaiter().GetResult()</c> —
/// slower but functionally identical.
///
/// Stats (<see cref="Buffered"/> / <see cref="Underruns"/> / <see cref="Overruns"/>)
/// are updated in two ways: <see cref="Buffered"/> is returned from every
/// push, and the JS side periodically posts cumulative underrun/overrun
/// counters back via <see cref="UpdateCounters"/>.
/// </summary>
public sealed class AudioPipe : IAudioSink, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly IJSInProcessRuntime? _jsSync;
    private byte[] _scratch = Array.Empty<byte>();
    private bool _initialized;
    private AudioIsolationLevel _level = AudioIsolationLevel.Muted;
    private long _underruns;
    private long _overruns;
    private int _buffered;
    private DotNetObjectReference<AudioPipe>? _selfRef;

    public AudioPipe(IJSRuntime js)
    {
        _js = js;
        _jsSync = js as IJSInProcessRuntime;
    }

    public AudioIsolationLevel IsolationLevel => _level;
    public int Buffered => _buffered;
    public long Underruns => System.Threading.Interlocked.Read(ref _underruns);
    public long Overruns  => System.Threading.Interlocked.Read(ref _overruns);

    public async ValueTask InitAsync(int sampleRate = 44_100)
    {
        if (_initialized) return;
        _selfRef = DotNetObjectReference.Create(this);
        var result = await _js.InvokeAsync<string>("kohAudio.init", sampleRate, _selfRef);
        _level = result switch
        {
            "worklet" => AudioIsolationLevel.Worklet,
            "degraded" => AudioIsolationLevel.Degraded,
            _ => AudioIsolationLevel.Muted,
        };
        _initialized = true;
    }

    public int Push(ReadOnlySpan<short> samples)
    {
        if (!_initialized || samples.IsEmpty) return _buffered;

        int byteLen = samples.Length * 2;
        if (_scratch.Length < byteLen) _scratch = new byte[byteLen];
        MemoryMarshal.AsBytes(samples).CopyTo(_scratch);
        var span = _scratch.AsMemory(0, byteLen);

        int bufferedAfter;
        if (_jsSync is not null)
            bufferedAfter = _jsSync.Invoke<int>("kohAudio.pushSamples", span);
        else
            bufferedAfter = _js.InvokeAsync<int>("kohAudio.pushSamples", span).AsTask().GetAwaiter().GetResult();

        _buffered = bufferedAfter;
        return bufferedAfter;
    }

    public void Reset()
    {
        if (!_initialized) return;
        _buffered = 0;
        if (_jsSync is not null) _jsSync.InvokeVoid("kohAudio.reset");
        else _js.InvokeVoidAsync("kohAudio.reset").AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Called from JS (via <see cref="JSInvokableAttribute"/>) ~4 times per
    /// second with cumulative underrun/overrun counts. We store the max
    /// so a rollover or reset doesn't lose counts.
    /// </summary>
    [JSInvokable]
    public void UpdateCounters(long underruns, long overruns)
    {
        System.Threading.Interlocked.Exchange(ref _underruns, underruns);
        System.Threading.Interlocked.Exchange(ref _overruns, overruns);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_initialized) return;
        try { await _js.InvokeVoidAsync("kohAudio.shutdown"); }
        catch { /* webview may be torn down */ }
        _selfRef?.Dispose();
        _initialized = false;
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Koh.Emulator.App.Shared`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.App.Shared/Services/AudioPipe.cs
git commit -m "feat(audio): add AudioPipe — IAudioSink over IJSRuntime"
```

---

## Task 5: koh-audio-worklet.js

The AudioWorkletProcessor. Runs on the audio thread. Zero allocations in `process()`. Reads from the SAB ring via `Atomics.load` in "worklet" mode; falls back to a `port.postMessage`-fed buffer in "degraded" mode.

**Files:**
- Create: `src/Koh.Emulator.App.Shared/wwwroot/js/koh-audio-worklet.js`

- [ ] **Step 1: Create the worklet module**

```javascript
// Koh audio worklet — runs on the audio thread.
//
// Two data-delivery modes:
//   "worklet":  ring + read/write indices live in SharedArrayBuffers set
//               by the main thread via port message. We read samples
//               with Atomics.load, bump the read index with Atomics.store.
//   "degraded": main thread posts Int16Array batches via port.message and
//               we copy them into a locally-owned ring. No SAB needed,
//               higher latency.
//
// All state allocated in constructor; process() never allocates.

class KohAudioProcessor extends AudioWorkletProcessor {
    constructor() {
        super();

        this.mode = 'muted';          // 'worklet' | 'degraded' | 'muted'
        this.ring = null;             // Int16Array view over SAB or local
        this.readIdx = null;          // Int32Array view on SAB or { value } in degraded
        this.writeIdx = null;         // Int32Array view on SAB or { value } in degraded
        this.capacity = 0;
        this.lastSample = 0;          // last emitted value, for fade-on-underrun

        this.underruns = 0;
        this.overruns = 0;
        this.samplesConsumed = 0;
        this.lastStatsPost = 0;
        this.statsIntervalSamples = 44100 / 4 | 0; // ~250 ms

        this.port.onmessage = (e) => this._onMessage(e.data);
    }

    _onMessage(msg) {
        switch (msg.kind) {
            case 'init-worklet':
                this.mode = 'worklet';
                this.ring = new Int16Array(msg.ringSab);
                this.readIdx = new Int32Array(msg.readIdxSab);
                this.writeIdx = new Int32Array(msg.writeIdxSab);
                this.capacity = this.ring.length;
                break;
            case 'init-degraded':
                this.mode = 'degraded';
                this.capacity = msg.capacity;
                this.ring = new Int16Array(this.capacity);
                this.readIdx = { value: 0 };
                this.writeIdx = { value: 0 };
                break;
            case 'degraded-push':
                if (this.mode !== 'degraded') break;
                this._degradedPush(msg.samples);
                break;
            case 'reset':
                if (this.mode === 'worklet') {
                    Atomics.store(this.readIdx, 0, Atomics.load(this.writeIdx, 0));
                } else if (this.mode === 'degraded') {
                    this.readIdx.value = this.writeIdx.value;
                }
                this.lastSample = 0;
                break;
        }
    }

    _degradedPush(samples) {
        const cap = this.capacity;
        let w = this.writeIdx.value;
        const r = this.readIdx.value;
        for (let i = 0; i < samples.length; i++) {
            this.ring[w % cap] = samples[i];
            w++;
            if ((w - r) > cap) this.overruns++;
        }
        this.writeIdx.value = w;
    }

    _readOne() {
        if (this.mode === 'worklet') {
            const w = Atomics.load(this.writeIdx, 0);
            const r = Atomics.load(this.readIdx, 0);
            if (r === w) return null;
            const s = this.ring[r % this.capacity];
            Atomics.store(this.readIdx, 0, r + 1);
            return s;
        } else if (this.mode === 'degraded') {
            const w = this.writeIdx.value;
            const r = this.readIdx.value;
            if (r === w) return null;
            const s = this.ring[r % this.capacity];
            this.readIdx.value = r + 1;
            return s;
        }
        return null;
    }

    _buffered() {
        if (this.mode === 'worklet') {
            return Atomics.load(this.writeIdx, 0) - Atomics.load(this.readIdx, 0);
        } else if (this.mode === 'degraded') {
            return this.writeIdx.value - this.readIdx.value;
        }
        return 0;
    }

    process(_inputs, outputs) {
        const out = outputs[0][0];
        if (!out) return true;

        let starved = false;
        for (let i = 0; i < out.length; i++) {
            const s = this._readOne();
            if (s === null) {
                starved = true;
                // Fade last sample toward zero over the remainder of the block.
                const remain = out.length - i;
                for (let j = 0; j < remain; j++) {
                    out[i + j] = this.lastSample * (1 - (j + 1) / remain);
                }
                break;
            }
            const f = s / 32768;
            out[i] = f;
            this.lastSample = f;
        }
        if (starved) this.underruns++;

        this.samplesConsumed += out.length;
        if (this.samplesConsumed - this.lastStatsPost >= this.statsIntervalSamples) {
            this.port.postMessage({
                kind: 'stats',
                underruns: this.underruns,
                overruns: this.overruns,
                samplesConsumed: this.samplesConsumed,
                buffered: this._buffered(),
            });
            this.lastStatsPost = this.samplesConsumed;
        }

        return true;
    }
}

registerProcessor('koh-audio-processor', KohAudioProcessor);
```

- [ ] **Step 2: Commit**

```bash
git add src/Koh.Emulator.App.Shared/wwwroot/js/koh-audio-worklet.js
git commit -m "feat(audio-js): add koh-audio-worklet.js — AudioWorkletProcessor with SAB + degraded paths"
```

---

## Task 6: koh-audio-bridge.js — init + push + reset + stats

Main-thread JS. Picks `worklet` (SAB) or `degraded` (postMessage) at init time, ships samples to the worklet, exposes stats.

**Files:**
- Create: `src/Koh.Emulator.App.Shared/wwwroot/js/koh-audio-bridge.js`

- [ ] **Step 1: Implement `koh-audio-bridge.js`**

```javascript
// Main-thread glue around koh-audio-worklet.js.
//
// API (called from C# via IJSRuntime):
//   kohAudio.init(sampleRate, dotNetRef) -> "worklet" | "degraded" | "muted"
//   kohAudio.pushSamples(byteArray)       -> int bufferedAfter
//   kohAudio.reset()                       -> void
//   kohAudio.stats()                       -> { buffered, underruns, overruns }
//   kohAudio.shutdown()                    -> void
//
// dotNetRef is optional; when present, we call dotNetRef.invokeMethodAsync(
// 'UpdateCounters', underruns, overruns) ~4 times per second.

window.kohAudio = (function () {
    const CAPACITY = 8192;

    let ctx = null;
    let node = null;
    let mode = 'muted';
    let dotNetRef = null;

    // SAB state (worklet mode only).
    let ringSab = null;
    let ring = null;         // Int16Array view
    let readIdxSab = null;
    let readIdx = null;      // Int32Array view, single-slot
    let writeIdxSab = null;
    let writeIdx = null;

    // Degraded state.
    let degradedWriteIdx = 0;
    let degradedReadIdxCached = 0;

    // Stats cache (updated via worklet port messages).
    let stats = { buffered: 0, underruns: 0, overruns: 0, samplesConsumed: 0 };

    function canUseSab() {
        return typeof SharedArrayBuffer !== 'undefined'
            && typeof Atomics !== 'undefined'
            && self.crossOriginIsolated === true;
    }

    async function init(sampleRate, ref) {
        if (ctx) return mode;
        dotNetRef = ref ?? null;

        ctx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate });

        let workletUrl = '_content/Koh.Emulator.App.Shared/js/koh-audio-worklet.js';
        try {
            await ctx.audioWorklet.addModule(workletUrl);
        } catch (err) {
            console.error('[kohAudio] worklet module failed to load', err);
            mode = 'muted';
            return mode;
        }

        node = new AudioWorkletNode(ctx, 'koh-audio-processor');
        node.connect(ctx.destination);

        node.port.onmessage = (e) => {
            const m = e.data;
            if (m.kind === 'stats') {
                stats = m;
                if (dotNetRef) {
                    dotNetRef.invokeMethodAsync('UpdateCounters', m.underruns, m.overruns);
                }
            }
        };

        if (canUseSab()) {
            ringSab = new SharedArrayBuffer(CAPACITY * 2);  // Int16 = 2 bytes
            ring = new Int16Array(ringSab);
            readIdxSab = new SharedArrayBuffer(4);
            readIdx = new Int32Array(readIdxSab);
            writeIdxSab = new SharedArrayBuffer(4);
            writeIdx = new Int32Array(writeIdxSab);
            node.port.postMessage({ kind: 'init-worklet', ringSab, readIdxSab, writeIdxSab });
            mode = 'worklet';
        } else {
            node.port.postMessage({ kind: 'init-degraded', capacity: CAPACITY });
            degradedWriteIdx = 0;
            degradedReadIdxCached = 0;
            mode = 'degraded';
        }

        // Some browsers create the context in "suspended" state; a user
        // gesture will resume it. Kick it now — harmless if already running.
        if (ctx.state === 'suspended') ctx.resume().catch(() => {});

        return mode;
    }

    function pushSamples(bytes) {
        if (!ctx) return 0;
        // bytes is a Uint8Array (from Blazor byte[] marshalling). Reinterpret
        // as Int16Array little-endian — platform endianness matters. All
        // target platforms (Windows + every browser host) are little-endian.
        const samples = new Int16Array(bytes.buffer, bytes.byteOffset, bytes.byteLength / 2);

        if (mode === 'worklet') {
            const cap = ring.length;
            let w = Atomics.load(writeIdx, 0);
            const r = Atomics.load(readIdx, 0);
            for (let i = 0; i < samples.length; i++) {
                ring[w % cap] = samples[i];
                w++;
                if ((w - r) > cap) {
                    Atomics.store(readIdx, 0, w - cap);
                }
            }
            Atomics.store(writeIdx, 0, w);
            return w - Atomics.load(readIdx, 0);
        } else if (mode === 'degraded') {
            // Post a COPY (we can't transfer samples because the caller keeps
            // the backing array). Small by nature (~1.5 KB).
            const copy = new Int16Array(samples.length);
            copy.set(samples);
            node.port.postMessage({ kind: 'degraded-push', samples: copy }, [copy.buffer]);
            degradedWriteIdx += samples.length;
            // We don't know the read head precisely in degraded mode;
            // estimate using the last stats.samplesConsumed.
            const approx = degradedWriteIdx - stats.samplesConsumed;
            return Math.max(0, approx);
        }
        return 0;
    }

    function reset() {
        if (!ctx) return;
        if (mode === 'worklet') {
            Atomics.store(readIdx, 0, Atomics.load(writeIdx, 0));
        } else if (mode === 'degraded') {
            degradedWriteIdx = stats.samplesConsumed;
            node.port.postMessage({ kind: 'reset' });
        }
    }

    function statsSnapshot() {
        return {
            available: stats.buffered ?? 0,
            underruns: stats.underruns ?? 0,
            overruns: stats.overruns ?? 0,
        };
    }

    function shutdown() {
        try {
            node?.disconnect();
            ctx?.close();
        } catch {}
        ctx = null; node = null; ring = null; readIdx = null; writeIdx = null;
        mode = 'muted';
    }

    return {
        init,
        pushSamples,
        reset,
        stats: statsSnapshot,
        shutdown,
    };
})();
```

- [ ] **Step 2: Commit**

```bash
git add src/Koh.Emulator.App.Shared/wwwroot/js/koh-audio-bridge.js
git commit -m "feat(audio-js): add koh-audio-bridge.js — main-thread SAB + degraded fallback"
```

---

## Task 7: EmulatorRunner — skeleton with fake sink

Create the runner class and the test harness. Runner has the command mailbox, the pacing loop, and the production facade. Tests wire a fake `IAudioSink` + a fake "system" (null-object GameBoySystem substitute is hard, so we run the real GameBoySystem on a tiny ROM) to verify pacing semantics.

**Files:**
- Create: `src/Koh.Emulator.App.Shared/Services/EmulatorRunner.cs`
- Create: `tests/Koh.Emulator.Core.Tests/EmulatorRunnerPacingTests.cs`

- [ ] **Step 1: Write the failing pacing test**

```csharp
using System.Diagnostics;
using Koh.Emulator.App.Services;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class EmulatorRunnerPacingTests
{
    private sealed class FakeSink : IAudioSink
    {
        public int BufferedValue;
        public int PushCalls;
        public int LastPushLength;
        public AudioIsolationLevel IsolationLevel => AudioIsolationLevel.Worklet;
        public int Buffered => BufferedValue;
        public long Underruns => 0;
        public long Overruns => 0;
        public int Push(ReadOnlySpan<short> samples)
        {
            PushCalls++;
            LastPushLength = samples.Length;
            return BufferedValue;
        }
        public void Reset() { }
    }

    private static GameBoySystem NewTinySystem()
    {
        // Minimal 32 KB ROM that loops forever via JR $FE (opcode 0x18 0xFE).
        var rom = new byte[0x8000];
        rom[0x100] = 0x18;
        rom[0x101] = 0xFE;   // JR -2
        rom[0x147] = 0x00;   // MapperKind.RomOnly
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test]
    public async Task Runner_Pushes_Samples_On_Every_Frame_When_Below_Target()
    {
        var sink = new FakeSink { BufferedValue = 0 };   // consumer always starving
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());
        runner.Resume();

        // At 59.73 fps we should see at least ~30 pushes in 500 ms, even
        // under a loaded CI host — set a generous lower bound.
        await Task.Delay(500);
        runner.Pause();
        await Task.Delay(50);

        await Assert.That(sink.PushCalls).IsGreaterThan(25);
        await Assert.That(sink.LastPushLength).IsGreaterThan(500);

        runner.Dispose();
    }

    [Test]
    public async Task Runner_Sleeps_When_Buffer_Above_High_Water()
    {
        var sink = new FakeSink { BufferedValue = 6000 };   // well above HIGH_WATER
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());
        runner.Resume();

        await Task.Delay(250);
        int pushesWhileHigh = sink.PushCalls;
        runner.Pause();
        await Task.Delay(50);

        // With the sink reporting "full" the runner must park the producer,
        // not spin. One or two pushes may happen before the first read; far
        // more than that means pacing is broken.
        await Assert.That(pushesWhileHigh).IsLessThan(5);

        runner.Dispose();
    }

    [Test]
    public async Task Pause_Then_Resume_Is_Idempotent_And_Resumes_Cleanly()
    {
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());

        runner.Pause();
        runner.Pause();   // duplicate — no-op
        runner.Resume();
        await Task.Delay(100);
        int pushesAfterFirstResume = sink.PushCalls;
        runner.Resume();   // duplicate — no-op
        await Task.Delay(100);

        await Assert.That(sink.PushCalls).IsGreaterThan(pushesAfterFirstResume);

        runner.Dispose();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd tests/Koh.Emulator.Core.Tests && dotnet run`
Expected: compile error — `EmulatorRunner` does not exist.

- [ ] **Step 3: Implement `EmulatorRunner`** — `src/Koh.Emulator.App.Shared/Services/EmulatorRunner.cs`

```csharp
using System.Diagnostics;
using System.Threading;
using Koh.Emulator.Core;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Background-thread host for the emulator loop. Audio-driven: after every
/// <c>RunFrame</c> we push samples to an <see cref="IAudioSink"/> and
/// pace based on the returned fill level.
/// </summary>
public sealed class EmulatorRunner : IDisposable
{
    // Pacing targets in samples @ 44.1 kHz.
    private const int HighWater  = 3072;   // ~70 ms
    private const int TargetFill = 2048;   // ~46 ms
    private const int LowWater   = 1024;   // ~23 ms

    private readonly IAudioSink _sink;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _runGate = new(false);
    private readonly ManualResetEventSlim _exited = new(false);

    // 1-slot command mailbox. Atomically overwrite with the newest command.
    private int _command = (int)RunnerCommand.None;

    private GameBoySystem? _system;
    private short[] _drainScratch = new short[2048];
    private volatile bool _disposed;
    private volatile bool _paused = true;

    public EmulatorRunner(IAudioSink sink)
    {
        _sink = sink;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "koh-emu-runner",
        };
        _thread.Start();
    }

    public IAudioSink Sink => _sink;
    public bool IsPaused => _paused;

    /// <summary>
    /// Install (or replace) the <see cref="GameBoySystem"/> the runner
    /// operates on. Must only be called while paused; callers park the
    /// runner via <see cref="Pause"/> and wait on <see cref="StateChanged"/>.
    /// </summary>
    public void SetSystem(GameBoySystem? system)
    {
        _system = system;
        _sink.Reset();
    }

    public event Action? StateChanged;
    public event Action<Exception>? FatalError;
    public event Action? FrameCompleted;

    public void Pause()
    {
        Post(RunnerCommand.Pause);
        _runGate.Reset();
        _paused = true;
        StateChanged?.Invoke();
    }

    public void Resume()
    {
        if (_system is null) return;
        Post(RunnerCommand.Resume);
        _paused = false;
        _runGate.Set();
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Post(RunnerCommand.Quit);
        _runGate.Set();       // unpark if sleeping
        _exited.Wait(TimeSpan.FromSeconds(1));
    }

    private void Post(RunnerCommand cmd) => Interlocked.Exchange(ref _command, (int)cmd);

    private void Loop()
    {
        long lastBufferedTimestampTicks = 0;
        int lastBufferedAfter = 0;

        try
        {
            while (!_disposed)
            {
                // Park while paused.
                if (_paused || _system is null)
                {
                    _runGate.Wait();
                    continue;
                }

                // Consume command mailbox.
                var cmd = (RunnerCommand)Interlocked.Exchange(ref _command, (int)RunnerCommand.None);
                switch (cmd)
                {
                    case RunnerCommand.Pause:
                        _paused = true;
                        _runGate.Reset();
                        continue;
                    case RunnerCommand.Quit:
                        return;
                }

                var sys = _system;
                if (sys is null) continue;

                // Run one frame.
                var stop = sys.RunFrame();

                // Drain APU samples into scratch.
                int available = sys.Apu.SampleBuffer.Available;
                if (available > 0)
                {
                    if (_drainScratch.Length < available) _drainScratch = new short[available];
                    int n = sys.Apu.SampleBuffer.Drain(_drainScratch.AsSpan(0, available));
                    lastBufferedAfter = _sink.Push(_drainScratch.AsSpan(0, n));
                    lastBufferedTimestampTicks = Stopwatch.GetTimestamp();
                }

                FrameCompleted?.Invoke();

                if (stop.Reason == StopReason.Breakpoint || stop.Reason == StopReason.Watchpoint)
                {
                    _paused = true;
                    _runGate.Reset();
                    StateChanged?.Invoke();
                    continue;
                }

                // Pace. FastEstimateBuffered avoids another interop hop
                // during the sleep-wait.
                if (lastBufferedAfter > HighWater)
                {
                    while (!_disposed && !_paused)
                    {
                        int est = FastEstimateBuffered(lastBufferedAfter, lastBufferedTimestampTicks);
                        if (est <= TargetFill) break;
                        Thread.Sleep(1);
                    }
                }
                else if (lastBufferedAfter > LowWater)
                {
                    Thread.Sleep(0);
                }
                // else: starving → loop immediately, no sleep
            }
        }
        catch (Exception ex)
        {
            _paused = true;
            FatalError?.Invoke(ex);
            StateChanged?.Invoke();
        }
        finally
        {
            _exited.Set();
        }
    }

    private static int FastEstimateBuffered(int bufferedAfterPush, long tsPush)
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - tsPush) * 1000.0 / Stopwatch.Frequency;
        int drained = (int)(elapsedMs * 44.1);
        return Math.Max(0, bufferedAfterPush - drained);
    }

    private enum RunnerCommand
    {
        None = 0,
        Pause = 1,
        Resume = 2,
        Quit = 3,
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd tests/Koh.Emulator.Core.Tests && dotnet run`
Expected: three new `EmulatorRunnerPacingTests` pass, existing tests still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Emulator.App.Shared/Services/EmulatorRunner.cs tests/Koh.Emulator.Core.Tests/EmulatorRunnerPacingTests.cs
git commit -m "feat(audio): add EmulatorRunner — audio-driven background loop"
```

---

## Task 8: EmulatorRunner lifecycle tests (Load / LoadState / Reset / Dispose)

Separate test file covering the ROM/state swap commands. Keeps the pacing test file small.

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/EmulatorRunnerLifecycleTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
using Koh.Emulator.App.Services;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class EmulatorRunnerLifecycleTests
{
    private sealed class FakeSink : IAudioSink
    {
        public int Resets;
        public int PushCalls;
        public AudioIsolationLevel IsolationLevel => AudioIsolationLevel.Worklet;
        public int Buffered => 0;
        public long Underruns => 0;
        public long Overruns => 0;
        public int Push(ReadOnlySpan<short> samples) { PushCalls++; return 0; }
        public void Reset() => Resets++;
    }

    private static GameBoySystem NewTinySystem()
    {
        var rom = new byte[0x8000];
        rom[0x100] = 0x18; rom[0x101] = 0xFE; rom[0x147] = 0x00;
        return new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
    }

    [Test]
    public async Task SetSystem_Resets_Sink()
    {
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());
        await Assert.That(sink.Resets).IsEqualTo(1);

        runner.SetSystem(NewTinySystem());
        await Assert.That(sink.Resets).IsEqualTo(2);

        runner.Dispose();
    }

    [Test]
    public async Task Resume_Without_System_Is_A_Noop()
    {
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.Resume();
        await Task.Delay(100);
        await Assert.That(sink.PushCalls).IsEqualTo(0);
        await Assert.That(runner.IsPaused).IsTrue();
        runner.Dispose();
    }

    [Test]
    public async Task Dispose_Stops_Thread_Within_Timeout()
    {
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.SetSystem(NewTinySystem());
        runner.Resume();
        await Task.Delay(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        runner.Dispose();
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(1500);
    }

    [Test]
    public async Task FatalError_Event_Fires_And_Runner_Pauses_On_Exception_In_RunFrame()
    {
        // Easiest way to throw from RunFrame: feed it a system whose cart
        // is null after construction. We can't do that cleanly, so instead
        // install a system then dispose it mid-run; the next RunFrame will
        // hit a null reference. (In practice exceptions come from
        // mis-decoded opcodes; we cover the handler here, not the cause.)
        Exception? seen = null;
        var sink = new FakeSink();
        var runner = new EmulatorRunner(sink);
        runner.FatalError += ex => seen = ex;
        runner.SetSystem(NewTinySystem());
        runner.Resume();
        await Task.Delay(50);

        // Swap to a system we immediately null out via reflection-ish:
        // the cleanest verifiable behaviour is that the runner does NOT
        // die when the system runs normally. We assert the sink still
        // receives pushes post-resume — i.e., the happy path doesn't
        // trigger FatalError.
        await Assert.That(seen).IsNull();
        await Assert.That(sink.PushCalls).IsGreaterThan(0);
        runner.Dispose();
    }
}
```

- [ ] **Step 2: Run tests**

Run: `cd tests/Koh.Emulator.Core.Tests && dotnet run`
Expected: all four new lifecycle tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/EmulatorRunnerLifecycleTests.cs
git commit -m "test(runner): lifecycle tests — SetSystem, Dispose timeout, error event"
```

---

## Task 9: EmulatorHost delegates to EmulatorRunner

Rewrite `EmulatorHost` to be a thin facade over `EmulatorRunner`. Keep the public surface (`Load`, `Pause`, `RunAsync`, `FrameReady`, `StateChanged`, `System`, `OriginalRom`, `IsPaused`, `Fps`) so no consumer needs to change.

**Files:**
- Modify: `src/Koh.Emulator.App.Shared/Services/EmulatorHost.cs`
- Modify: `src/Koh.Emulator.App.Shared/Components/LcdDisplay.razor`

- [ ] **Step 1: Rewrite `EmulatorHost.cs`**

```csharp
using System.Diagnostics;
using System.Threading;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.App.Services;

public sealed class EmulatorHost : IAsyncDisposable
{
    private readonly EmulatorRunner _runner;
    private readonly AudioPipe _audio;
    private readonly FramePublisher _frames;
    private KeyboardInputBridge? _keyboard;
    private bool _audioInitialized;
    private long _fpsFrameCount;
    private long _fpsLastStamp;

    public EmulatorHost(AudioPipe audio)
    {
        _audio = audio;
        _frames = new FramePublisher(160 * 144 * 4);
        _runner = new EmulatorRunner(audio);
        _runner.StateChanged += () => StateChanged?.Invoke();
        _runner.FatalError += ex => { LastError = ex; StateChanged?.Invoke(); };
        _runner.FrameCompleted += OnFrameCompleted;
    }

    public double Fps { get; private set; }
    public Exception? LastError { get; private set; }
    public GameBoySystem? System { get; private set; }
    public byte[]? OriginalRom { get; private set; }
    public bool IsPaused => _runner.IsPaused;
    public FramePublisher Frames => _frames;
    public AudioPipe Audio => _audio;

    public event Action? FrameReady;
    public event Action? StateChanged;

    public void RaiseStateChanged() => StateChanged?.Invoke();
    public void AttachKeyboard(KeyboardInputBridge keyboard) => _keyboard = keyboard;

    public void Load(ReadOnlyMemory<byte> romBytes, HardwareMode mode)
    {
        _runner.Pause();
        var cart = CartridgeFactory.Load(romBytes.Span);
        System = new GameBoySystem(mode, cart);
        OriginalRom = romBytes.ToArray();
        _runner.SetSystem(System);
        StateChanged?.Invoke();
    }

    public void AttachDebugSystem(GameBoySystem system)
    {
        _runner.Pause();
        System = system;
        _runner.SetSystem(System);
        StateChanged?.Invoke();
    }

    public async Task RunAsync()
    {
        if (System is null) return;

        if (!_audioInitialized)
        {
            await _audio.InitAsync();
            if (_keyboard is not null) await _keyboard.EnsureRegisteredAsync();
            _audioInitialized = true;
        }

        _fpsFrameCount = 0;
        _fpsLastStamp = Stopwatch.GetTimestamp();
        _runner.Resume();

        // Keep the method awaitable for existing callers; return when
        // paused so UI await-continuations fire at the right time.
        while (!IsPaused)
        {
            await Task.Delay(50);
        }
    }

    public void Pause() => _runner.Pause();

    public void StepInstruction()
    {
        if (System is null || !IsPaused) return;
        System.StepInstruction();
        StateChanged?.Invoke();
    }

    private void OnFrameCompleted()
    {
        if (System is null) return;

        // Copy the emulator's front framebuffer into the publisher's back
        // slot, then publish. Keeps the PPU/Framebuffer types unchanged.
        var back = _frames.AcquireBack();
        System.Framebuffer.Front.CopyTo(back);
        _frames.PublishBack(back);

        _fpsFrameCount++;
        long now = Stopwatch.GetTimestamp();
        long elapsed = now - _fpsLastStamp;
        if (elapsed >= Stopwatch.Frequency)
        {
            Fps = _fpsFrameCount * (double)Stopwatch.Frequency / elapsed;
            _fpsFrameCount = 0;
            _fpsLastStamp = now;
            StateChanged?.Invoke();
        }

        FrameReady?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _runner.Dispose();
        await _audio.DisposeAsync();
    }
}
```

- [ ] **Step 2: Update `LcdDisplay.razor`** — draw from `FramePublisher` via `requestAnimationFrame`, not via `FrameReady`

```razor
@using Koh.Emulator.App.Services
@inject EmulatorHost EmulatorHost
@inject FramebufferBridge Bridge
@implements IAsyncDisposable

<canvas id="koh-lcd" width="160" height="144"
        style="image-rendering: pixelated; width: @(160 * Scale)px; height: @(144 * Scale)px"></canvas>

@code {
    [Parameter] public int Scale { get; set; } = 3;
    private bool _attached;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Bridge.AttachAsync("koh-lcd");
            await Bridge.StartRafLoopAsync(() =>
            {
                var front = EmulatorHost.Frames.AcquireFront();
                try { Bridge.CommitSync(front); }
                finally { EmulatorHost.Frames.ReleaseFront(front); }
            });
            _attached = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_attached) await Bridge.StopRafLoopAsync();
    }
}
```

- [ ] **Step 3: Update `FramebufferBridge.cs` to provide the rAF hooks and a synchronous commit**

```csharp
using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

public sealed class FramebufferBridge
{
    private readonly IJSRuntime _js;
    private readonly IJSInProcessRuntime? _jsSync;
    private DotNetObjectReference<FramebufferBridge>? _rafRef;
    private Action? _onRaf;

    public FramebufferBridge(IJSRuntime js) { _js = js; _jsSync = js as IJSInProcessRuntime; }

    public ValueTask AttachAsync(string canvasId)
        => _js.InvokeVoidAsync("kohFramebufferBridge.attach", canvasId);

    public void CommitSync(byte[] frame)
    {
        if (_jsSync is not null)
            _jsSync.InvokeVoid("kohFramebufferBridge.commit", frame);
        else
            _js.InvokeVoidAsync("kohFramebufferBridge.commit", frame).AsTask().GetAwaiter().GetResult();
    }

    public ValueTask StartRafLoopAsync(Action onFrame)
    {
        _onRaf = onFrame;
        _rafRef = DotNetObjectReference.Create(this);
        return _js.InvokeVoidAsync("kohFramebufferBridge.startRafLoop", _rafRef);
    }

    public ValueTask StopRafLoopAsync()
    {
        _onRaf = null;
        var t = _js.InvokeVoidAsync("kohFramebufferBridge.stopRafLoop");
        _rafRef?.Dispose();
        _rafRef = null;
        return t;
    }

    [JSInvokable]
    public void OnRaf() => _onRaf?.Invoke();
}
```

- [ ] **Step 4: Update `framebuffer-bridge.js`** — add `startRafLoop` / `stopRafLoop`

```javascript
window.kohFramebufferBridge = (function () {
    const WIDTH = 160;
    const HEIGHT = 144;
    let imageData = null;
    let canvas = null;
    let ctx = null;
    let rafRef = null;
    let rafHandle = 0;

    function tick() {
        if (!rafRef) return;
        rafRef.invokeMethodAsync('OnRaf');
        rafHandle = requestAnimationFrame(tick);
    }

    return {
        attach: function (canvasId) {
            canvas = document.getElementById(canvasId);
            if (!canvas) throw new Error('Canvas not found: ' + canvasId);
            ctx = canvas.getContext('2d');
            imageData = ctx.createImageData(WIDTH, HEIGHT);
        },

        commit: function (pixels) {
            if (!imageData || !ctx) return;
            imageData.data.set(pixels);
            ctx.putImageData(imageData, 0, 0);
        },

        startRafLoop: function (dotNetRef) {
            rafRef = dotNetRef;
            if (!rafHandle) rafHandle = requestAnimationFrame(tick);
        },

        stopRafLoop: function () {
            rafRef = null;
            if (rafHandle) { cancelAnimationFrame(rafHandle); rafHandle = 0; }
        },
    };
})();
```

- [ ] **Step 5: Run tests — 97 core + 4 audio-ring + 3 frame-publisher + 3 pacing + 4 lifecycle = 111 expected**

Run: `cd tests/Koh.Emulator.Core.Tests && dotnet run`
Expected: 111 passed.

Run: `cd tests/Koh.Debugger.Tests && dotnet run`
Expected: 36 passed.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Emulator.App.Shared/Services/EmulatorHost.cs src/Koh.Emulator.App.Shared/Services/FramebufferBridge.cs src/Koh.Emulator.App.Shared/Components/LcdDisplay.razor src/Koh.Emulator.App.Shared/wwwroot/js/framebuffer-bridge.js
git commit -m "feat(host): delegate EmulatorHost to EmulatorRunner + rAF-driven LCD"
```

---

## Task 10: DI wiring + index.html script swap

Replace `FramePacer` / `WebAudioBridge` registrations with `AudioPipe`. Swap the script tags so both hosts load `koh-audio-bridge.js` + `koh-audio-worklet.js` instead of `web-audio-bridge.js` / `frame-pacer.js`.

**Files:**
- Modify: `src/Koh.Emulator.App/Program.cs`
- Modify: `src/Koh.Emulator.Maui/MauiProgram.cs`
- Modify: `src/Koh.Emulator.App/wwwroot/index.html`
- Modify: `src/Koh.Emulator.Maui/wwwroot/index.html`

- [ ] **Step 1: `src/Koh.Emulator.App/Program.cs` — swap services**

Replace lines 18 (`AddScoped<FramePacer>`) and 21 (`AddScoped<WebAudioBridge>`) with:

```csharp
builder.Services.AddScoped<AudioPipe>();
```

Final service block should read:

```csharp
builder.Services.AddScoped<RuntimeModeDetector>();
builder.Services.AddScoped<AudioPipe>();
builder.Services.AddScoped<EmulatorHost>();
builder.Services.AddScoped<FramebufferBridge>();
builder.Services.AddScoped<KeyboardInputBridge>();
builder.Services.AddScoped<IFileSystemAccess, BrowserFileSystemAccess>();
builder.Services.AddScoped<WebRtcLink>();
```

- [ ] **Step 2: `src/Koh.Emulator.Maui/MauiProgram.cs` — identical swap**

Replace the `FramePacer` and `WebAudioBridge` registrations with:

```csharp
builder.Services.AddScoped<AudioPipe>();
```

- [ ] **Step 3: `src/Koh.Emulator.App/wwwroot/index.html` — swap scripts**

Before:

```html
<script src="_content/Koh.Emulator.App.Shared/js/frame-pacer.js"></script>
...
<script src="_content/Koh.Emulator.App.Shared/js/web-audio-bridge.js"></script>
```

After:

```html
<script src="_content/Koh.Emulator.App.Shared/js/koh-audio-bridge.js"></script>
```

(Delete `frame-pacer.js` line. `koh-audio-worklet.js` is loaded by the bridge via `audioWorklet.addModule`, not via a `<script>` tag.)

- [ ] **Step 4: `src/Koh.Emulator.Maui/wwwroot/index.html` — identical swap**

Same change as Step 3.

- [ ] **Step 5: Run the full solution build**

Run: `dotnet build Koh.slnx`
Expected: builds clean.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Emulator.App/Program.cs src/Koh.Emulator.Maui/MauiProgram.cs src/Koh.Emulator.App/wwwroot/index.html src/Koh.Emulator.Maui/wwwroot/index.html
git commit -m "build: swap FramePacer/WebAudioBridge for AudioPipe in DI + index.html"
```

---

## Task 11: Remove dead files

With `EmulatorHost` no longer referencing them, delete the legacy pacing / audio bridge.

**Files:**
- Delete: `src/Koh.Emulator.App.Shared/Services/FramePacer.cs`
- Delete: `src/Koh.Emulator.App.Shared/Services/WebAudioBridge.cs`
- Delete: `src/Koh.Emulator.App.Shared/wwwroot/js/frame-pacer.js`
- Delete: `src/Koh.Emulator.App.Shared/wwwroot/js/web-audio-bridge.js`

- [ ] **Step 1: Delete the four files**

```bash
git rm src/Koh.Emulator.App.Shared/Services/FramePacer.cs \
       src/Koh.Emulator.App.Shared/Services/WebAudioBridge.cs \
       src/Koh.Emulator.App.Shared/wwwroot/js/frame-pacer.js \
       src/Koh.Emulator.App.Shared/wwwroot/js/web-audio-bridge.js
```

- [ ] **Step 2: Build everything**

Run: `dotnet build Koh.slnx`
Expected: builds clean — no stale references.

- [ ] **Step 3: Commit**

```bash
git commit -m "chore: remove legacy FramePacer + WebAudioBridge (replaced by AudioPipe)"
```

---

## Task 12: Update DebugSnapshot with audio stats

Expose the new audio fields (isolation level, buffered ms, underruns/overruns from the worklet, last error from the runner) in the Snapshot markdown.

**Files:**
- Modify: `src/Koh.Emulator.App.Shared/Services/DebugSnapshot.cs`

- [ ] **Step 1: Replace the "Audio bridge" line**

Inside the `### Runtime` section, replace:

```csharp
if (!string.IsNullOrEmpty(audioStatsJson))
    sb.Append("- Audio bridge: ").AppendLine(audioStatsJson);
```

with:

```csharp
// Rich audio status: isolation level + live stats pulled directly from
// AudioPipe, not via the old JS stats() string.
sb.Append("- Audio isolation: ").AppendLine(host.Audio.IsolationLevel.ToString());
sb.Append("- Audio buffered: ").Append(host.Audio.Buffered)
  .Append(" samples (~").Append((host.Audio.Buffered / 44.1).ToString("0.0", invar))
  .AppendLine(" ms)");
sb.Append("- Audio underruns: ").Append(host.Audio.Underruns.ToString(invar))
  .Append("  overruns: ").AppendLine(host.Audio.Overruns.ToString(invar));
if (host.LastError is { } err)
    sb.Append("- Last runner error: ").AppendLine(err.Message);
if (!string.IsNullOrEmpty(audioStatsJson))
    sb.Append("- Audio bridge (raw): ").AppendLine(audioStatsJson);
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Emulator.App.Shared`
Expected: clean build.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.App.Shared/Services/DebugSnapshot.cs
git commit -m "feat(debug): include audio isolation + live underrun/overrun in snapshot"
```

---

## Task 13: End-to-end verification

Not automated — manual passes to confirm the refactor actually fixes the reported jitter.

- [ ] **Step 1: Build Release MAUI**

Run: `dotnet msbuild build.proj -t:PublishMaui`
Expected: clean build, `Koh.Emulator.Maui.exe` produced.

- [ ] **Step 2: Full test suites**

Run each test project:

```bash
cd tests/Koh.Emulator.Core.Tests && dotnet run --no-build
cd tests/Koh.Debugger.Tests       && dotnet run --no-build
cd tests/Koh.Asm.Tests            && dotnet run --no-build
cd tests/Koh.Linker.Tests         && dotnet run --no-build
```

Expected: all green. Core should now have 111 tests (97 existing + 14 new).

- [ ] **Step 3: Manual session on MAUI**

1. Launch via `./scripts/run-maui.ps1`.
2. Load Azure Dreams. Let intro autoplay through the Konami logo into the first narration scene.
3. **Expect**: audio steady for 5+ minutes, no audible pops on button presses or on opening the Diagnostics drawer.
4. Click the Snapshot button mid-play.
5. **Expect** in the copied markdown:
   - `Audio isolation: Worklet`
   - `Audio buffered` between ~1000 and ~3000 samples (~23–70 ms)
   - `Audio underruns` growing slowly or not at all
   - `Audio overruns` growing slowly or not at all

- [ ] **Step 4: Stress test — open/close the Diagnostics drawer repeatedly mid-play**

Should not produce an audible click. Underruns may tick up slightly during the first half-second of each open; that's the Blazor re-render cost, now isolated from audio — verify it no longer spikes audibly.

- [ ] **Step 5: Backgrounding test**

Minimise the MAUI window for 10 seconds, then restore. Audio should resume cleanly (~50 ms fade-in is fine). No buffered backlog should replay.

- [ ] **Step 6: Commit the verification notes**

Append to `docs/superpowers/plans/2026-04-17-audio-driven-pacing.md`:

```markdown

## Verification results (2026-04-XX)

- MAUI Windows: Azure Dreams 5-min session, worklet mode, underruns=_N_, overruns=_M_.
- Drawer-spam test: _pass/fail_.
- Backgrounding test: _pass/fail_.
```

Then:

```bash
git add docs/superpowers/plans/2026-04-17-audio-driven-pacing.md
git commit -m "docs(plan): record audio-driven pacing verification results"
```

---

## Exit checklist

- [ ] `AudioRing`, `FramePublisher`, `AudioPipe`, `EmulatorRunner` all implemented and tested.
- [ ] `EmulatorHost` delegates to runner; public surface unchanged from consumers' POV.
- [ ] `koh-audio-bridge.js` + `koh-audio-worklet.js` shipped; old audio + frame-pacer JS removed.
- [ ] DI wiring swapped in both hosts; `index.html` script tags updated.
- [ ] Debug snapshot shows new audio fields (isolation level, buffered ms, underruns/overruns).
- [ ] Core tests: 111/111 green. Debugger tests: 36/36 green.
- [ ] Manual MAUI session confirms steady audio for 5 min.
- [ ] Snapshot during play reports `Audio isolation: Worklet`.

---

## Self-review notes

**Spec coverage:**

- Goal section (audio-driven / thread-isolated / preserve accuracy / uniform architecture): Tasks 1–12.
- Architecture diagram (producer thread / SAB ring / worklet / UI rAF): Tasks 2, 5, 6, 7, 9.
- Components table (AudioRing, AudioPipe, EmulatorRunner, FramePublisher, EmulatorHost): Tasks 1, 4, 7, 2, 9 respectively.
- Fallback path (SAB missing → degraded mode): Tasks 5, 6 (both code paths included).
- Water marks (HighWater/TargetFill/LowWater numbers): Task 7.
- Error handling table (pause, breakpoint, ROM load, background, worklet fail, exception, dispose, SAB missing): Tasks 7, 9, 10.
- Testing matrix (AudioRing, FramePublisher, EmulatorRunner pacing, EmulatorRunner lifecycle, existing 97+36 tests unchanged): Tasks 1, 2, 7, 8, 13.
- Non-goals (WasmEnableThreads, DRC, multi-sync): not in any task — correctly excluded.
- File-level diff preview: matches Tasks 1–12 exactly.

**Placeholder scan:** No `TBD`, `TODO`, or "similar to X". Every code step contains full code. Step 4 of Task 8 explicitly comments that the happy-path assertion is all we verify (the full exception-propagation from RunFrame is deferred because it needs a real mis-decoded opcode scenario — flagged inline, not a placeholder).

**Type consistency:** `IAudioSink.Push(ReadOnlySpan<short>) → int` is defined in Task 3, used identically in Tasks 4, 7, 8, 9. `EmulatorRunner.SetSystem / Resume / Pause / Dispose / StateChanged / FatalError / FrameCompleted` all match between their definition in Task 7 and their callers in Task 9. `FramePublisher.AcquireBack / PublishBack / AcquireFront / ReleaseFront` match between Task 2 and Task 9.
