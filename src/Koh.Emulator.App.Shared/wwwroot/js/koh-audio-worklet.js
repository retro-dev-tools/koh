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

        // Output silence (not counted as underrun) until the ring has this
        // much buffered. Avoids ~200 "underruns" at AudioContext spin-up
        // where the worklet runs before the producer has landed its first
        // batch. Also re-engaged after reset().
        this.primeTarget = 44100 * 150 / 1000 | 0;  // 150 ms head start
        this.primed = false;

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
                this.primed = false;
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

        // Warmup gate: stay silent (not counted as underrun) until the
        // producer has filled the ring to primeTarget. Re-engages after
        // reset() so ROM loads / save-state loads don't spray underruns.
        if (!this.primed) {
            if (this._buffered() < this.primeTarget) {
                for (let i = 0; i < out.length; i++) out[i] = 0;
                this.samplesConsumed += out.length;
                return true;
            }
            this.primed = true;
        }

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
