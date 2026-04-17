# Koh Emulator — Audio-Driven Pacing + AudioWorklet Migration

**Date:** 2026-04-17
**Status:** Accepted — pending implementation plan
**Supersedes:** the ad-hoc rAF-paced audio path introduced in Phase 4

## Goal

Replace the current display-driven audio pipeline (`requestAnimationFrame`
→ `RunFrame` → `ScriptProcessorNode` on the main thread) with an
**audio-driven, thread-isolated** pipeline that matches how mature
emulator front-ends (mGBA, SameBoy, byuu's higan/ares, RetroArch) have
synchronised audio for two decades. Concrete targets:

- **Eliminate the ~2.4 underruns/sec** observed under normal play on
  Azure Dreams — the immediate user-reported bug.
- **Survive heavy main-thread work** (Blazor re-render, GC pause,
  `LcdDisplay` commit) without an audible glitch.
- **Preserve 100% hardware accuracy.** Each `RunFrame()` call must
  produce byte-identical internal state to the current implementation;
  pacing is a property of the loop, not the emulator.
- **Uniform architecture in MAUI Blazor Hybrid and Blazor WASM.** Same
  C# classes, same JS modules, only the transport differs for the
  audio batches. No `WasmEnableThreads` dependency yet (that's an
  orthogonal follow-up if/when playground perf demands it).

The thesis ("audio is the clock") is established emulator-dev practice;
see the research at the bottom of this spec for citations.

## Non-goals

- `WasmEnableThreads`-based direct SAB sharing between .NET and JS
  (possible upgrade later; not needed for the uniform architecture).
- Dynamic rate control / audio resampling to match host audio clock
  precisely (follow-up; the fractional APU accumulator already gives us
  an exact 44 100 Hz producer rate, so drift against the host is
  negligible).
- Multi-sync (separate audio+video threads with swap-chain
  compositing). Overkill for a single-canvas emulator.
- Changes to the emulator core (`GameBoySystem`, `Apu`, `Ppu`). The
  refactor is entirely above the `RunFrame()` boundary.

## Architecture

Three independent lanes, each lock-free at its hot-path boundary:

```
┌──────────────────────── SAME IN WASM + MAUI ────────────────────────┐

[Producer thread]  ── C# background Thread, one per EmulatorHost
    while (running):
        check command slot (Pause / Resume / Load / LoadState / Quit)
        sys.RunFrame()                               # produces ~738 samples
        InvokeSync("kohAudio.pushSamples", bytes)    # returns bufferedAfter
        Interlocked.Exchange(ref _publishedFrame, back)
        pace(bufferedAfter)                          # sleep / tight-loop / yield

[JS main thread]
    kohAudio.pushSamples(bytes):
        decode LE Int16 → SAB ring, bump write index via Atomics.store
        return current fill

[Audio thread — AudioWorkletProcessor]
    process(out):
        for each frame in out:
            read SAB ring via Atomics.load
            if starved: fade last sample to 0 across the remainder
        periodically port.postMessage({underruns, overruns})  # batched

[UI thread]
    rAF:
        var buf = Volatile.Read(ref _publishedFrame)
        canvas.putImageData(buf, 0, 0)
```

Key property: **the audio thread never depends on the UI thread**, and
**the UI thread never depends on the audio thread**. Blazor re-renders,
GC pauses, layout passes — none of them can glitch the audio.

## Components

### C# — `Koh.Emulator.App.Shared`

| Component | Responsibility |
|---|---|
| `AudioRing` | Lock-free SPSC ring of `short` samples (used only for internal tests, not in the hot path — the producer pushes directly to JS). |
| `AudioPipe` | Owns the `IJSInProcessRuntime` handle + reusable `byte[]` scratch. Exposes `int Push(ReadOnlySpan<short> samples)` which marshals into the scratch in LE and calls `kohAudio.pushSamples(bytes) → int bufferedAfter`. |
| `EmulatorRunner` | Background `Thread` named `"koh-emu-runner"`. Owns the audio-driven loop. Consumes commands from a lock-free 1-slot mailbox (`Pause` / `Resume` / `LoadRom` / `LoadState` / `Reset` / `Quit`). Publishes stats (FPS, buffered ms, underruns, overruns, isolation level). |
| `FramePublisher` | Triple-buffered `byte[]` (drawing / published / held-by-UI). `PublishBack()` on producer side, `TryAcquireFront()` on UI. Uses `Interlocked.Exchange`; no locks. |
| `EmulatorHost` | Public facade unchanged from the consumer's point of view (`Load`, `Pause`, `RunAsync`, `FrameReady`, `StateChanged`). Internally delegates to `EmulatorRunner` via the command mailbox; `RunAsync` becomes a thin wrapper that posts `Resume` and waits on `StateChanged`. |

**Removed:** `FramePacer.cs` (rAF pacing is gone), current per-frame
`StateChanged` fan-out.

### JS — `src/Koh.Emulator.App.Shared/wwwroot/js`

| File | Responsibility |
|---|---|
| `koh-audio-bridge.js` | Replaces `web-audio-bridge.js`. Creates the `AudioContext`, loads the worklet module, allocates SAB + two `Int32Array` index SABs, wires the worklet node. Exposes `init(sampleRate)`, `pushSamples(bytes) → int bufferedAfter`, `reset()`, `stats()`, `shutdown()`. |
| `koh-audio-worklet.js` | `AudioWorkletProcessor` subclass. Zero allocations in `process()`. Reads samples from the SAB ring; fades last sample toward 0 on underrun. Reports `underruns` / `overruns` to main thread once per ~250 ms via `port.postMessage`. |

**Removed:** `web-audio-bridge.js` (replaced), `frame-pacer.js` (no more
rAF-driven emulator pacing; UI rAF is a 5-line inline loop in
`LcdDisplay`).

### Fallback path

If `typeof SharedArrayBuffer === 'undefined'` or the page is not
cross-origin-isolated (`self.crossOriginIsolated === false`), the
bridge initialises in **degraded mode**:

- No SAB. Ring lives in a plain `Int16Array` owned by the main thread.
- Worklet reads via `port.postMessage` with transferable-buffer batches.
- Higher latency, more jitter, but functional.
- `AudioPipe.IsolationLevel` is reported as `Degraded` to the snapshot.

Main use case: the GitHub Pages playground before the deploy workflow
serves `COOP: same-origin` + `COEP: require-corp`. MAUI BlazorWebView
controls its own virtual origin and will always be isolated.

## Data flow & pacing numbers

**SAB ring capacity**: 8192 samples (≈ 186 ms @ 44.1 kHz). Only real
buffer in the system.

**Water marks:**

| Mark | Samples | ms | Producer behaviour |
|---|---|---|---|
| HIGH_WATER | 3072 | 70 | `Thread.Sleep(1)` in a loop until estimated fill back to TARGET. |
| TARGET_FILL | 2048 | 46 | Steady-state goal. |
| LOW_WATER | 1024 | 23 | No sleep — loop tight. |

**Producer single-iteration**:

```
1. Poll command mailbox.
2. sys.RunFrame()                              ≈ 17 ms emulated
3. Drain APU samples into scratch byte[].
4. bufferedAfter = kohAudio.pushSamples(bytes) # synchronous, IJSInProcessRuntime
5. FramePublisher.PublishBack()
6. pace():
     if bufferedAfter > HIGH_WATER:
         while FastEstimateBuffered(...) > TARGET_FILL: Thread.Sleep(1)
     else if bufferedAfter > LOW_WATER:
         Thread.Sleep(0)                       # yield slice
     else:
         ;                                     # starving, loop
```

`FastEstimateBuffered` starts from the last-known `bufferedAfter` and
subtracts `elapsed_ms × 44.1` to skip another interop hop during the
sleep-wait.

**End-to-end latency**: ~46 ms typical, ~70 ms worst case.

**Stats channel** (worklet → main → C#):

- Counters bumped in the worklet: `underruns`, `overruns`,
  `samplesConsumed`.
- Posted every ~250 ms via `port.postMessage({u, o, s})` — NOT per
  event (no allocations in `process`).
- Main thread caches latest. `kohAudio.stats()` returns the snapshot
  without an interop round-trip.

## Error handling & lifecycle

| Event | Behaviour |
|---|---|
| User pauses / breakpoint hits | Producer finishes current `RunFrame`, sees `Pause` in mailbox, parks on a `ManualResetEventSlim`. SAB drains (~46 ms), worklet fades to silence. |
| User resumes | `Set` the event → producer re-enters loop. SAB rebuilds within ~50 ms. |
| ROM / save-state load | Producer parks → host installs new `System` → `kohAudio.reset()` clears SAB → producer resumes. No stale audio. |
| Tab/window backgrounded | Browser may suspend `AudioContext`. Hook `ctx.onstatechange`: on `suspended`, park producer; on `running`, resume. No catch-up spike. |
| `AudioContext` needs user gesture | Bridge is initialised lazily on the first `Resume` (same gesture chain the existing code uses). |
| Worklet module fails to load | Fall back to silent-run: producer still paces off `Stopwatch` at 44.1 kHz, audio is muted, `IsolationLevel = Muted`. Error surfaced to UI. |
| Uncaught exception in `RunFrame` | Producer catches, stores on `EmulatorHost.LastError`, transitions to Paused, fires `StateChanged`. Thread does not die. |
| Dispose | `EmulatorHost.DisposeAsync` posts `Quit`; producer exits, `Thread.Join(1s)`. JS side `audioContext.close()`. |
| SAB / COOP+COEP missing | Bridge degrades to `port.postMessage` batches; `IsolationLevel = Degraded`. |
| Duplicate Run / Pause command | Idempotent no-op. |

## Testing

Tests **do not go through `EmulatorRunner`**. The unit of emulation is
still `GameBoySystem.RunFrame()`, which is what the existing 97 core
tests and the Blargg / Mooneye / acid2 harnesses call directly. The
runner is the audio layer around that, and is tested separately.

| Test project / file | What it verifies |
|---|---|
| `Koh.Emulator.Core.Tests/AudioRingTests` | SPSC correctness. 1 M samples pushed on thread A, drained on thread B, arrive in order, no drops at capacity, `available` accurate. |
| `Koh.Emulator.Core.Tests/FramePublisherTests` | UI consumer may see any recent frame but never a torn one; producer never blocks on consumer. |
| `Koh.Emulator.Core.Tests/EmulatorRunnerPacingTests` | With a fake `IAudioSink` that reports a configurable fill level, the runner produces the expected number of `RunFrame` calls per wall-clock interval (± tolerance). Crucially: pacing follows audio fill, not wall time. Simulate a backgrounded tab (sink returns "full") — runner must park, not spin. |
| `Koh.Emulator.Core.Tests/EmulatorRunnerLifecycleTests` | Load → Pause → Resume → LoadState → Pause → Dispose; each transition deterministic and the producer thread exits cleanly. |
| Existing `Koh.Emulator.Core.Tests` (97 tests) | Unchanged, must stay green. |
| Existing `Koh.Debugger.Tests` (36 tests) | Unchanged. Breakpoint / watchpoint paths still use the command-mailbox to park the runner. |
| Existing `Koh.Compat.Tests` (Blargg / Mooneye / acid2) | Unchanged. These drive `RunFrame` directly; pacing doesn't apply. |

Manual verification:

- Load Azure Dreams, play through intro + into first town — audio stays
  steady for 5+ minutes.
- Open/close debug drawer (heavy Blazor render) mid-play — no audible
  click.
- Background the MAUI window, wait 10 s, foreground — audio resumes
  without a buffered backlog.
- Snapshot during play — new `AudioPipe` fields present (BufferedMs,
  IsolationLevel, Underruns/Overruns counters).

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| `IJSInProcessRuntime` synchronous call from a background thread in MAUI may require dispatching to the main thread. | If so, the Push becomes `await InvokeVoidAsync` with `ValueTask` round-tripping — still fine at 60/s; budget the extra 0.1 ms per call. |
| SAB ring fill returned to the producer can lag by up to one batch. | The pacing algorithm is explicitly tolerant: TARGET_FILL is 2048 samples, HIGH_WATER 3072. One stale read can't push us past the high water on its own. |
| AudioWorklet not supported in older WebView2 builds shipped with MAUI. | Modern WebView2 (bundled with .NET 10 MAUI workload) supports AudioWorklet. Fallback path covers the edge case. |
| COOP/COEP on GitHub Pages. | Adding `_headers`/Cloudflare-style headers isn't supported by GitHub Pages natively. Either add a service worker that synthesises the headers (well-known trick) or accept Degraded mode on the playground until we host elsewhere. |
| Introducing threads invites race bugs. | Two well-defined SPSC boundaries (`AudioRing`, `FramePublisher`); command mailbox is a 1-slot `Interlocked.Exchange`. No shared mutable state otherwise. All boundaries covered by the new tests. |

## File-level diff preview

```
Added:
  src/Koh.Emulator.App.Shared/Services/AudioPipe.cs
  src/Koh.Emulator.App.Shared/Services/AudioRing.cs
  src/Koh.Emulator.App.Shared/Services/EmulatorRunner.cs
  src/Koh.Emulator.App.Shared/Services/FramePublisher.cs
  src/Koh.Emulator.App.Shared/wwwroot/js/koh-audio-bridge.js
  src/Koh.Emulator.App.Shared/wwwroot/js/koh-audio-worklet.js
  tests/Koh.Emulator.Core.Tests/AudioRingTests.cs
  tests/Koh.Emulator.Core.Tests/FramePublisherTests.cs
  tests/Koh.Emulator.Core.Tests/EmulatorRunnerPacingTests.cs
  tests/Koh.Emulator.Core.Tests/EmulatorRunnerLifecycleTests.cs

Modified:
  src/Koh.Emulator.App.Shared/Services/EmulatorHost.cs   (delegates to runner)
  src/Koh.Emulator.App.Shared/Services/FramebufferBridge.cs (reads from FramePublisher)
  src/Koh.Emulator.App.Shared/Components/LcdDisplay.razor (simple rAF redraw)
  src/Koh.Emulator.App.Shared/Services/DebugSnapshot.cs   (new audio stats fields)
  src/Koh.Emulator.App/wwwroot/index.html                  (swap audio scripts)
  src/Koh.Emulator.Maui/wwwroot/index.html                 (ditto)

Removed:
  src/Koh.Emulator.App.Shared/Services/FramePacer.cs
  src/Koh.Emulator.App.Shared/Services/WebAudioBridge.cs
  src/Koh.Emulator.App.Shared/wwwroot/js/web-audio-bridge.js
  src/Koh.Emulator.App.Shared/wwwroot/js/frame-pacer.js
```

## Research notes — "audio is the clock"

- [redream — Improving Audio/Video Synchronization with Multi Sync](https://redream.io/posts/improving-audio-video-synchronization-multi-sync) — primary "why audio should drive the clock" argument; display refresh rates are fixed and uncontrollable, audio rates are controllable.
- [NESdev forum — How to properly playback audio when creating an emulator](https://forums.nesdev.org/viewtopic.php?t=15383) — Nemulator author: "Most emulators sync to the audio playback rate and adjust the video rate to accommodate."
- [Libretro docs — Dynamic Rate Control](https://docs.libretro.com/development/cores/dynamic-rate-control/) — the formal follow-up to audio-driven pacing; imperceptible pitch shift to match host exactly.
- [Chrome Developers — AudioWorklet design pattern](https://developer.chrome.com/blog/audio-worklet-design-pattern/) — the SAB + Worker pattern, explicitly cites emulators as the canonical use case.
- [Loke.dev — Stop Allocating Inside the AudioWorkletProcessor](https://loke.dev/blog/stop-allocating-inside-audioworkletprocessor) — why zero-alloc `process()` is non-negotiable; 2.9 ms budget at 128-sample blocks.
- [padenot/ringbuf.js](https://github.com/padenot/ringbuf.js/) — the reference lock-free SPSC SAB ring implementation.

## Out of scope for this plan

- Dynamic Rate Control (host-audio resampling to exact device rate).
  Would be a follow-up. Useful if we later observe drift against the
  host audio device — right now the fractional APU accumulator keeps
  the producer rate within fractions of a percent of 44 100 Hz, and
  the drop-oldest/fade-on-underrun ring absorbs the rest.
- `WasmEnableThreads`-based direct SAB writes from .NET to JS. Would
  save one interop hop per frame in WASM; the interop hop is ~1 ms at
  worst and we do it 60×/s, so not worth the complexity until/unless
  profiling shows otherwise.
- Web Worker-hosted emulator core in WASM. Blazor-in-Worker has sharp
  edges; revisit when/if the playground is the primary distribution
  channel.
