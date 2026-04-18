// Main-thread glue around koh-audio-worklet.js.
//
// API (called from C# via IJSRuntime):
//   kohAudio.init(sampleRate, dotNetRef) -> "worklet" | "degraded" | "muted"
//   kohAudio.pushSamples(byteArray)       -> int bufferedAfter
//   kohAudio.reset()                       -> void
//   kohAudio.stats()                       -> { buffered, underruns, overruns }
//   kohAudio.shutdown()                    -> void
//
// dotNetRef is optional; when present we call
// dotNetRef.invokeMethodAsync('UpdateCounters', underruns, overruns) ~4 Hz.

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

        const workletUrl = '_content/Koh.Emulator.App.Shared/js/koh-audio-worklet.js';
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
            mode = 'degraded';
        }

        if (ctx.state === 'suspended') ctx.resume().catch(() => {});

        return mode;
    }

    function pushSamples(bytes) {
        if (!ctx) return 0;
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
            const copy = new Int16Array(samples.length);
            copy.set(samples);
            node.port.postMessage({ kind: 'degraded-push', samples: copy }, [copy.buffer]);
            degradedWriteIdx += samples.length;
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
