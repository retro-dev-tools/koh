import * as assert from 'assert';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import type { ChildProcess } from 'child_process';
import { DAP_LISTENING_MARKER, waitForDapReady } from '../debug/waitForDapReady';

/**
 * Stand-in for child_process.ChildProcess. `waitForDapReady` only
 * uses `.stdout`, `.stderr`, `on('exit')` and `off(...)`, so a plain
 * EventEmitter with PassThrough streams covers the contract without
 * spawning a real process.
 */
function fakeChild(): {
    child: ChildProcess;
    stdout: PassThrough;
    stderr: PassThrough;
    emitExit: (code: number | null) => void;
    emitError: (err: Error) => void;
} {
    const ee: any = new EventEmitter();
    ee.stdout = new PassThrough();
    ee.stderr = new PassThrough();
    // EventEmitter supports removeListener; ChildProcess exposes it as off().
    // Node aliases these at runtime but the types don't — shim for this mock.
    ee.off = ee.removeListener.bind(ee);
    return {
        child: ee as ChildProcess,
        stdout: ee.stdout,
        stderr: ee.stderr,
        emitExit: (code) => ee.emit('exit', code, null),
        emitError: (err) => ee.emit('error', err),
    };
}

suite('waitForDapReady', () => {
    test('resolves when marker arrives in a single stdout chunk', async () => {
        const { child, stdout } = fakeChild();
        const ready = waitForDapReady(child, 5_000);
        stdout.write(`${DAP_LISTENING_MARKER} \\\\.\\pipe\\koh-dap-abc\n`);
        await ready;
    });

    test('resolves when marker arrives on stderr instead of stdout', async () => {
        const { child, stderr } = fakeChild();
        const ready = waitForDapReady(child, 5_000);
        stderr.write(`${DAP_LISTENING_MARKER} /tmp/koh-dap-abc.sock\n`);
        await ready;
    });

    test('resolves when marker is split across two chunks', async () => {
        // Real child_process pipes chunk at OS buffer boundaries, not
        // line boundaries. The original implementation ran
        // `includes(marker)` on each chunk individually and missed
        // this case — the bug this test is the regression guard for.
        const { child, stdout } = fakeChild();
        const ready = waitForDapReady(child, 5_000);
        stdout.write('[koh-');
        stdout.write('dap] listening on \\\\.\\pipe\\koh-dap-abc\n');
        await ready;
    });

    test('ignores unrelated noise before the marker', async () => {
        const { child, stdout } = fakeChild();
        const ready = waitForDapReady(child, 5_000);
        stdout.write('Silk.NET.OpenAL booting\n');
        stdout.write('GLFW: window shown\n');
        stdout.write(`${DAP_LISTENING_MARKER} \\\\.\\pipe\\koh-dap-abc\n`);
        await ready;
    });

    test('rejects when the emulator exits before the marker', async () => {
        const { child, emitExit } = fakeChild();
        const ready = waitForDapReady(child, 5_000);
        emitExit(127);
        await assert.rejects(ready, /exited \(code=127\).*before DAP server was listening/);
    });

    test('rejects immediately when the emulator spawn emits error', async () => {
        const { child, emitError } = fakeChild();
        const ready = waitForDapReady(child, 5_000);
        emitError(new Error('spawn ENOENT'));
        await assert.rejects(ready, /spawn ENOENT/);
    });

    test('rejects when the timeout fires before the marker', async () => {
        const { child } = fakeChild();
        const ready = waitForDapReady(child, 50);
        await assert.rejects(ready, /didn't start within 50ms/);
    });

    test('does not double-settle after exit-then-marker', async () => {
        const { child, stdout, emitExit } = fakeChild();
        const ready = waitForDapReady(child, 5_000);
        emitExit(1);
        // Late marker should be ignored, not cause an UnhandledRejection.
        stdout.write(`${DAP_LISTENING_MARKER} x\n`);
        await assert.rejects(ready, /exited \(code=1\)/);
    });

    test('survives >4KB of noise before the marker', async () => {
        const { child, stdout } = fakeChild();
        const ready = waitForDapReady(child, 5_000);
        stdout.write('x'.repeat(4096) + '\n');
        stdout.write(`${DAP_LISTENING_MARKER} ok\n`);
        await ready;
    });
});
