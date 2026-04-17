// Fixed-ring-buffer WebAudio bridge. Bounds latency so a slow frame can't
// build up seconds of audio lag, zero-allocates per push, and pads with
// silence on underrun instead of hard-failing.
//
// ScriptProcessorNode is deprecated but still works in WebView2 and Blazor
// WASM; migrating to AudioWorklet is a larger project. The ring below
// eliminates the worst of the glitching by keeping the producer + consumer
// decoupled and never reallocating.
window.kohWebAudio = (function () {
    const CAPACITY   = 16384;   // ~372 ms @ 44100 — ample slack
    const HIGH_WATER = 8192;    // ~186 ms — start dropping samples above this
    const BLOCK      = 2048;    // ScriptProcessor buffer size; larger = fewer wakeups

    let ctx = null;
    let scriptNode = null;
    let ring = null;
    let readIdx = 0;
    let writeIdx = 0;
    let available = 0;
    let underruns = 0;
    let overruns = 0;

    function read1() {
        const v = ring[readIdx];
        readIdx = (readIdx + 1) & (CAPACITY - 1);
        available--;
        return v;
    }

    function write1(v) {
        ring[writeIdx] = v;
        writeIdx = (writeIdx + 1) & (CAPACITY - 1);
        available++;
    }

    return {
        init: function (sampleRate) {
            if (ctx) return;
            ctx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate });
            ring = new Float32Array(CAPACITY);
            readIdx = 0; writeIdx = 0; available = 0;
            scriptNode = ctx.createScriptProcessor(BLOCK, 0, 1);
            scriptNode.onaudioprocess = function (e) {
                const out = e.outputBuffer.getChannelData(0);
                const n = Math.min(out.length, available);
                for (let i = 0; i < n; i++) out[i] = read1();
                if (n < out.length) {
                    // Starved: fade last sample toward zero instead of a hard
                    // click to a DC-zero gap. Not perfect, but less jarring.
                    const last = n > 0 ? out[n - 1] : 0;
                    for (let i = n; i < out.length; i++) {
                        const t = (i - n) / Math.max(1, out.length - n);
                        out[i] = last * (1 - t);
                    }
                    underruns++;
                }
            };
            scriptNode.connect(ctx.destination);
        },

        pushSamples: function (samples) {
            if (!ring) return;
            const len = samples.length;
            for (let i = 0; i < len; i++) {
                if (available >= HIGH_WATER) {
                    // Drop oldest to bound latency. Keeps the emulator from
                    // building up seconds of lag when the frame pacer runs
                    // faster than real time.
                    read1();
                    overruns++;
                }
                write1(samples[i]);
            }
        },

        shutdown: function () {
            if (scriptNode) { scriptNode.disconnect(); scriptNode = null; }
            if (ctx) { ctx.close(); ctx = null; }
            ring = null; readIdx = 0; writeIdx = 0; available = 0;
        },

        // Diagnostics — handy from the dev console.
        stats: function () { return { available, underruns, overruns }; },
    };
})();
