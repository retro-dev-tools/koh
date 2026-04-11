/**
 * FIFO queue for DAP messages with boot buffering. Messages submitted before
 * the webview signals "ready" are queued and flushed when ready fires.
 * See design §11.9.
 */
export class DapMessageQueue {
    private buffered: unknown[] = [];
    private ready = false;
    private sink: ((msg: unknown) => void) | null = null;

    markReady(sink: (msg: unknown) => void): void {
        this.sink = sink;
        this.ready = true;
        for (const msg of this.buffered) sink(msg);
        this.buffered = [];
    }

    enqueueOutbound(msg: unknown, sendIfReady: (msg: unknown) => void): void {
        if (this.ready && this.sink) {
            sendIfReady(msg);
        } else {
            this.buffered.push(msg);
        }
    }

    reset(): void {
        this.buffered = [];
        this.ready = false;
        this.sink = null;
    }
}
