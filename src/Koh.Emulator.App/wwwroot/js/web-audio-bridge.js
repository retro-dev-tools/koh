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
