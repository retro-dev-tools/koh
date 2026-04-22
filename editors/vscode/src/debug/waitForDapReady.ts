import type { ChildProcess } from 'child_process';

/** Emulator prints this once NamedPipeServerStream is awaiting a client. */
export const DAP_LISTENING_MARKER = '[koh-dap] listening on';

/**
 * Resolve when the spawned emulator prints its "listening on" banner
 * on stdout or stderr, reject if the process exits early or the
 * timeout fires.
 *
 * Why it's complicated:
 *   - Chunks on ChildProcess stdio aren't aligned to line boundaries.
 *     The marker could arrive as `"[koh-"` + `"dap] listening on ..."`
 *     across two `data` events; naive `includes(marker)` on each chunk
 *     would miss it, leaving the user staring at a "connect ENOENT"
 *     dialog while the emulator is actually up. We accumulate into a
 *     rolling buffer trimmed to `marker.length` so unbounded growth
 *     is bounded even if the emulator floods output.
 *   - We listen on both stdout and stderr because the emulator routes
 *     diagnostics to stderr on some platforms and a log-level change
 *     would silently break us if we only watched stdout.
 *
 * Split out of KohDapAdapterFactory so this race-prone wait is
 * unit-testable with a fake EventEmitter-based ChildProcess — no VS
 * Code test host needed for the hot-path test.
 */
export function waitForDapReady(child: ChildProcess, timeoutMs: number): Promise<void> {
    return new Promise((resolve, reject) => {
        let done = false;
        let buffer = '';
        // Trim the rolling buffer to something comfortably larger than
        // the marker itself so we never miss a marker that straddles a
        // trim boundary, but small enough that a chatty emulator can't
        // balloon memory.
        const keepTail = DAP_LISTENING_MARKER.length * 4;

        const settle = (err?: Error) => {
            if (done) return;
            done = true;
            clearTimeout(timer);
            child.stdout?.off('data', onData);
            child.stderr?.off('data', onData);
            child.off('exit', onExit);
            err ? reject(err) : resolve();
        };
        const onData = (data: Buffer | string) => {
            buffer += Buffer.isBuffer(data) ? data.toString('utf8') : data;
            if (buffer.includes(DAP_LISTENING_MARKER)) {
                settle();
                return;
            }
            if (buffer.length > keepTail) {
                buffer = buffer.slice(-keepTail);
            }
        };
        const onExit = (code: number | null) => settle(
            new Error(`emulator exited (code=${code}) before DAP server was listening`),
        );
        const timer = setTimeout(
            () => settle(new Error(`emulator DAP server didn't start within ${timeoutMs}ms`)),
            timeoutMs,
        );

        child.stdout?.on('data', onData);
        child.stderr?.on('data', onData);
        child.on('exit', onExit);
    });
}
