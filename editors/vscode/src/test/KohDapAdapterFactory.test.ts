import * as assert from 'assert';
import * as vscode from 'vscode';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import type { ChildProcess, SpawnOptions } from 'child_process';
import { KohDapAdapterFactory, Spawner } from '../debug/KohDapAdapterFactory';
import { DAP_LISTENING_MARKER } from '../debug/waitForDapReady';
import { Logger } from '../core/Logger';
import { ToolchainResolver } from '../toolchain/ToolchainResolver';
import { ToolchainLocation } from '../toolchain/types';

/**
 * Captures one spawn invocation and exposes the fake child so tests
 * can script its stdout/stderr. Mirrors the subset of ChildProcess
 * that KohDapAdapterFactory actually uses.
 */
interface SpawnCall {
    readonly command: string;
    readonly args: readonly string[];
    readonly options: SpawnOptions;
    readonly child: ChildProcess;
    readonly stdout: PassThrough;
    readonly stderr: PassThrough;
    emitExit(code: number | null): void;
    emitError(err: Error): void;
}

function makeSpawner(): { spawner: Spawner; calls: SpawnCall[] } {
    const calls: SpawnCall[] = [];
    const spawner: Spawner = (command, args, options) => {
        const ee: any = new EventEmitter();
        ee.stdout = new PassThrough();
        ee.stderr = new PassThrough();
        ee.off = ee.removeListener.bind(ee);
        calls.push({
            command,
            args,
            options,
            child: ee as ChildProcess,
            stdout: ee.stdout,
            stderr: ee.stderr,
            emitExit: code => ee.emit('exit', code, null),
            emitError: err => ee.emit('error', err),
        });
        return ee as ChildProcess;
    };
    return { spawner, calls };
}

/**
 * Test double for ToolchainResolver. The factory only calls .resolve()
 * to find the emulator binary, so we don't need to subclass the whole
 * class — a minimal object that exposes that method is enough.
 */
function fakeResolver(location: ToolchainLocation | null): ToolchainResolver {
    return { resolve: () => location } as unknown as ToolchainResolver;
}

function fakeSession(configuration: vscode.DebugConfiguration): vscode.DebugSession {
    return { configuration } as unknown as vscode.DebugSession;
}

suite('KohDapAdapterFactory', () => {
    // Shared for the suite — see ToolchainResolver.test.ts for why
    // per-test Logger disposal trips VS Code's channel proxy cache.
    let log: Logger;
    suiteSetup(() => { log = new Logger('KohDap.Test'); });
    suiteTeardown(() => { log.dispose(); });

    test('rejects a launch config that has no program', async () => {
        const { spawner } = makeSpawner();
        const factory = new KohDapAdapterFactory(log, fakeResolver(null), spawner);
        const session = fakeSession({ type: 'koh', request: 'launch', name: 'x' });

        await assert.rejects(
            Promise.resolve(factory.createDebugAdapterDescriptor(session)),
            /missing "program"/,
        );
    });

    test('rejects with a clear "install toolchain" error when no emulator is resolvable', async () => {
        // No toolchain + no setting + no launch override → the factory
        // can't spawn anything, and the user needs a message that
        // actually tells them what to do.
        const { spawner, calls } = makeSpawner();
        const factory = new KohDapAdapterFactory(log, fakeResolver(null), spawner);
        const session = fakeSession({ type: 'koh', request: 'launch', name: 'x', program: '/roms/foo.gb' });

        await assert.rejects(
            Promise.resolve(factory.createDebugAdapterDescriptor(session)),
            /Install the toolchain|koh\.emulator\.exePath/,
        );
        assert.strictEqual(calls.length, 0, 'must not spawn when no emulator path resolves');
    });

    test('spawns the emulator with --dap=<pipe> and the ROM path', async () => {
        const { spawner, calls } = makeSpawner();
        const toolchain = fakeResolver({
            version: '0.1.3-beta',
            binDir: '/fake/bin',
            source: 'managedInstall',
        });
        const factory = new KohDapAdapterFactory(log, toolchain, spawner);

        const session = fakeSession({ type: 'koh', request: 'launch', name: 'x', program: '/roms/foo.gb' });
        const resultPromise = factory.createDebugAdapterDescriptor(session);

        // Descriptor can't resolve until the emulator prints the banner.
        assert.strictEqual(calls.length, 1, 'spawn should fire synchronously in createDebugAdapterDescriptor');
        const call = calls[0];
        assert.ok(call.command.includes('Koh.Emulator.App'), `unexpected command: ${call.command}`);
        assert.strictEqual(call.args.length, 2);
        assert.ok(/^--dap=koh-dap-/.test(call.args[0]), `unexpected first arg: ${call.args[0]}`);
        assert.strictEqual(call.args[1], '/roms/foo.gb');

        // Feed the marker so the promise can settle — otherwise the
        // test hangs for the 15s timeout.
        call.stdout.write(`${DAP_LISTENING_MARKER} \\\\.\\pipe\\x\n`);
        await resultPromise;
    });

    test('descriptor resolves only after the emulator prints the listening banner', async () => {
        // This is the regression guard for the named-pipe race that
        // caused "connect ENOENT \\\\.\\pipe\\koh-dap-..." dialogs. The
        // factory must not hand VS Code a DebugAdapterNamedPipeServer
        // before the emulator has actually started listening.
        const { spawner, calls } = makeSpawner();
        const factory = new KohDapAdapterFactory(log, fakeResolver({
            version: 'dev', binDir: '/fake/bin', source: 'managedInstall',
        }), spawner);

        const session = fakeSession({ type: 'koh', request: 'launch', name: 'x', program: '/roms/foo.gb' });
        const resultPromise = factory.createDebugAdapterDescriptor(session);

        // Nudge the event loop a few times while withholding the marker;
        // the descriptor promise must remain pending.
        let settled = false;
        void resultPromise.then(() => { settled = true; });
        for (let i = 0; i < 3; i++) await new Promise(r => setImmediate(r));
        assert.strictEqual(settled, false, 'descriptor resolved before emulator printed the banner');

        // Now emit the marker — promise should settle promptly.
        calls[0].stdout.write(`${DAP_LISTENING_MARKER} x\n`);
        const desc = await resultPromise;
        assert.ok(desc instanceof vscode.DebugAdapterNamedPipeServer, 'expected a named-pipe server descriptor');
    });

    test('descriptor resolves when the marker is split across two stdout chunks', async () => {
        // Real child_process pipes chunk at OS buffer boundaries, not
        // line boundaries. Our waitForDapReady accumulates a rolling
        // buffer so cross-chunk markers still match — this test exists
        // because the narrower single-chunk match silently broke F5 in
        // production.
        const { spawner, calls } = makeSpawner();
        const factory = new KohDapAdapterFactory(log, fakeResolver({
            version: 'dev', binDir: '/fake/bin', source: 'managedInstall',
        }), spawner);

        const session = fakeSession({ type: 'koh', request: 'launch', name: 'x', program: '/roms/foo.gb' });
        const resultPromise = factory.createDebugAdapterDescriptor(session);

        calls[0].stdout.write('[koh-');
        calls[0].stdout.write('dap] listening on \\\\.\\pipe\\x\n');

        await resultPromise;
    });

    test('honours launch.emulatorPath override even when toolchain is missing', async () => {
        // Per-session override lets a user point at a locally-built
        // emulator without having a toolchain installed; the factory
        // should trust the override and skip the "install toolchain"
        // error path.
        const { spawner, calls } = makeSpawner();
        const factory = new KohDapAdapterFactory(log, fakeResolver(null), spawner);

        const session = fakeSession({
            type: 'koh', request: 'launch', name: 'x',
            program: '/roms/foo.gb',
            emulatorPath: '/custom/Koh.Emulator.App',
        });
        const resultPromise = factory.createDebugAdapterDescriptor(session);

        assert.strictEqual(calls.length, 1);
        assert.strictEqual(calls[0].command, '/custom/Koh.Emulator.App');

        calls[0].stdout.write(`${DAP_LISTENING_MARKER} x\n`);
        await resultPromise;
    });

    test('terminateSession kills the child and drops it from the tracking map', async () => {
        // Regression guard for the "emulator stays running after VS Code
        // closes" bug. The factory must track the spawned child by
        // session id and SIGTERM it when the debug session ends;
        // otherwise the emulator process orphans (especially on Windows
        // where child procs are not killed with their parent).
        const { spawner, calls } = makeSpawner();
        const factory = new KohDapAdapterFactory(log, fakeResolver({
            version: 'dev', binDir: '/fake/bin', source: 'managedInstall',
        }), spawner);

        const session = fakeSession({ type: 'koh', request: 'launch', name: 's1', program: '/roms/foo.gb' });
        // Nudge the factory by pretending VS Code wired session.id = 's1-id'.
        (session as { id: string }).id = 's1-id';

        const resultPromise = factory.createDebugAdapterDescriptor(session);
        calls[0].stdout.write(`${DAP_LISTENING_MARKER} x\n`);
        await resultPromise;

        // Spy on kill — the fake child inherits from EventEmitter, so
        // attach a kill() method that records invocation and flips the
        // `killed` flag like a real ChildProcess would.
        let killed = false;
        (calls[0].child as unknown as { kill: (sig?: string) => boolean }).kill = () => { killed = true; return true; };
        Object.defineProperty(calls[0].child, 'killed', { get: () => killed });
        Object.defineProperty(calls[0].child, 'exitCode', { get: () => killed ? 0 : null });

        factory.terminateSession('s1-id');
        assert.strictEqual(killed, true, 'terminateSession must kill the tracked child');

        // Second call is a no-op (child.exitCode is non-null now).
        factory.terminateSession('s1-id');
    });

    test('dispose terminates every tracked session', async () => {
        // Two concurrent debug sessions — e.g. user opened two
        // launch.json configs — must BOTH be torn down on extension
        // deactivate, not just the first.
        const { spawner, calls } = makeSpawner();
        const factory = new KohDapAdapterFactory(log, fakeResolver({
            version: 'dev', binDir: '/fake/bin', source: 'managedInstall',
        }), spawner);

        const sA = fakeSession({ type: 'koh', request: 'launch', name: 'A', program: '/a.gb' });
        const sB = fakeSession({ type: 'koh', request: 'launch', name: 'B', program: '/b.gb' });
        (sA as { id: string }).id = 'A-id';
        (sB as { id: string }).id = 'B-id';

        const [pA, pB] = [
            factory.createDebugAdapterDescriptor(sA),
            factory.createDebugAdapterDescriptor(sB),
        ];
        for (const c of calls) c.stdout.write(`${DAP_LISTENING_MARKER} x\n`);
        await Promise.all([pA, pB]);

        const killed = [false, false];
        for (let i = 0; i < calls.length; i++) {
            const idx = i;
            (calls[i].child as unknown as { kill: () => boolean }).kill = () => { killed[idx] = true; return true; };
            Object.defineProperty(calls[i].child, 'killed', { get: () => killed[idx] });
            Object.defineProperty(calls[i].child, 'exitCode', { get: () => killed[idx] ? 0 : null });
        }

        factory.dispose();
        assert.deepStrictEqual(killed, [true, true], 'both children should be killed on dispose');
    });

    test('rejects when the emulator exits before the banner appears', async () => {
        // Catches "emulator path is wrong / crashes immediately" — the
        // user should see the real exit code in the error, not a
        // downstream ENOENT dialog.
        const { spawner, calls } = makeSpawner();
        const factory = new KohDapAdapterFactory(log, fakeResolver({
            version: 'dev', binDir: '/fake/bin', source: 'managedInstall',
        }), spawner);

        const session = fakeSession({ type: 'koh', request: 'launch', name: 'x', program: '/roms/foo.gb' });
        const resultPromise = factory.createDebugAdapterDescriptor(session);

        calls[0].emitExit(127);

        await assert.rejects(resultPromise, /exited \(code=127\)/);
    });
});
