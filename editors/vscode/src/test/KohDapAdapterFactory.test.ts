import * as assert from 'assert';
import * as vscode from 'vscode';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import * as net from 'net';
import os from 'os';
import * as path from 'path';
import type { ChildProcess, SpawnOptions } from 'child_process';
import { spawn } from 'child_process';
import * as fs from 'fs';
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
        assert.ok(call.command.includes('koh-emulator'), `unexpected command: ${call.command}`);
        assert.strictEqual(call.args.length, 2);
        assert.ok(call.args[0].startsWith('--dap='), `unexpected first arg: ${call.args[0]}`);
        assert.ok(path.basename(call.args[0].slice(6)).startsWith('koh-dap-'));
        assert.strictEqual(call.args[1], '/roms/foo.gb');

        // Feed the marker so the promise can settle — otherwise the
        // test hangs for the 15s timeout.
        call.stdout.write(`${DAP_LISTENING_MARKER} \\\\.\\pipe\\x\n`);
        await resultPromise;
    });

    for (const longTmpdir of [false, true]) {
        test(`descriptor connects to the endpoint passed to the emulator${longTmpdir ? ' with a long multibyte TMPDIR' : ''}`, async () => {
            const { spawner, calls } = makeSpawner();
            const factory = new KohDapAdapterFactory(log, fakeResolver(null), spawner);
            const previousTmpdir = os.tmpdir;
            let pending: Promise<vscode.DebugAdapterDescriptor>;
            try {
                if (longTmpdir) os.tmpdir = () => `/tmp/${'界'.repeat(40)}`;
                if (longTmpdir && process.platform !== 'win32') {
                    assert.ok(Buffer.byteLength(os.tmpdir(), 'utf8') > 103);
                }
                pending = factory.createDebugAdapterDescriptor(fakeSession({
                    type: 'koh', request: 'launch', name: 'transport',
                    program: '/roms/foo.gb', emulatorPath: '/fake/emulator',
                }));
            } finally {
                os.tmpdir = previousTmpdir;
            }
            calls[0].stdout.write(`${DAP_LISTENING_MARKER} ready\n`);
            const descriptor = await pending as vscode.DebugAdapterNamedPipeServer;
            const pipe = calls[0].args[0].slice('--dap='.length);
            const endpoint = process.platform === 'win32' ? `\\\\.\\pipe\\${pipe}` : pipe;
            if (process.platform !== 'win32') {
                assert.ok(path.isAbsolute(pipe), 'Unix host must receive an explicit socket path');
                assert.ok(Buffer.byteLength(pipe, 'utf8') <= 103, 'socket path exceeds the macOS limit');
            }
            const server = net.createServer(socket => socket.end('connected'));
            try {
                await new Promise<void>((resolve, reject) => {
                    server.once('error', reject);
                    server.listen(endpoint, resolve);
                });
                const message = await new Promise<string>((resolve, reject) => {
                    const client = net.connect(descriptor.path);
                    let received = '';
                    client.setEncoding('utf8');
                    client.setTimeout(2000, () => client.destroy(new Error('connection timed out')));
                    client.once('error', reject);
                    client.on('data', data => { received += data; });
                    client.once('end', () => resolve(received));
                });
                assert.strictEqual(message, 'connected');
            } finally {
                await new Promise<void>(resolve => server.close(() => resolve()));
            }
        });
    }

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
            emulatorPath: '/custom/koh-engine',
        });
        const resultPromise = factory.createDebugAdapterDescriptor(session);

        assert.strictEqual(calls.length, 1);
        assert.strictEqual(calls[0].command, '/custom/koh-engine');

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

    test('real desktop factory debugs native source and closes on disconnect', async function () {
        this.timeout(60_000);
        const desktop = process.env.KOH_TEST_DESKTOP;
        if (!desktop) { this.skip(); return; }
        const rom = process.env.KOH_TEST_DAP_ROM;
        const elf = process.env.KOH_TEST_DAP_ELF;
        assert.ok(rom && elf, 'KOH_TEST_DAP_ROM and KOH_TEST_DAP_ELF are required');
        for (const file of [desktop, rom, elf]) assert.ok(fs.existsSync(file), file);
        assert.match(path.basename(desktop), /^koh-emulator(?:\.exe)?$/);
        let child: ChildProcess | undefined;
        let exited: Promise<number | null> | undefined;
        let diagnostics = '';
        const spawner: Spawner = (command, args, options) => {
            assert.strictEqual(path.resolve(command), path.resolve(desktop));
            child = spawn(command, args, options);
            child.stderr?.on('data', data => { diagnostics = (diagnostics + String(data)).slice(-32768); });
            exited = new Promise(resolve => child!.once('exit', resolve));
            return child;
        };
        const factory = new KohDapAdapterFactory(log, fakeResolver({
            version: 'native-test', binDir: path.dirname(desktop), source: 'managedInstall',
        }), spawner);
        let socket: net.Socket | undefined;
        try {
            const session = fakeSession({ type: 'koh', request: 'launch', name: 'native-test', program: rom });
            (session as { id: string }).id = 'native-real-desktop';
            const descriptor = await factory.createDebugAdapterDescriptor(session) as vscode.DebugAdapterNamedPipeServer;
            socket = net.createConnection(descriptor.path);
            let transportError: Error | undefined;
            socket.on('error', error => { transportError = error; });
            socket.setTimeout(20_000, () => socket!.destroy(new Error('DAP transport timeout')));
            let pending = Buffer.alloc(0);
            const messages: any[] = [];
            socket.on('data', data => {
                try {
                    pending = Buffer.concat([pending, typeof data === 'string' ? Buffer.from(data) : data]);
                    assert.ok(pending.length <= 1024 * 1024, 'oversized DAP reply');
                    while (true) {
                        const end = pending.indexOf('\r\n\r\n');
                        if (end < 0) break;
                        const match = /Content-Length:\s*(\d+)/i.exec(pending.subarray(0, end).toString('ascii'));
                        assert.ok(match, 'missing DAP Content-Length');
                        const length = Number(match[1]);
                        if (pending.length < end + 4 + length) break;
                        messages.push(JSON.parse(pending.subarray(end + 4, end + 4 + length).toString('utf8')));
                        pending = pending.subarray(end + 4 + length);
                    }
                } catch (error) { transportError = error as Error; }
            });
            const take = async (predicate: (message: any) => boolean) => {
                const deadline = Date.now() + 20_000;
                while (true) {
                    const index = messages.findIndex(predicate);
                    if (index >= 0) return messages.splice(index, 1)[0];
                    if (transportError) throw transportError;
                    assert.ok(Date.now() < deadline, `DAP message timeout\n${diagnostics}`);
                    await new Promise(resolve => setTimeout(resolve, 10));
                }
            };
            let sequence = 0;
            const request = async (command: string, args: object = {}) => {
                const seq = ++sequence;
                const body = Buffer.from(JSON.stringify({ seq, type: 'request', command, arguments: args }));
                socket!.write(`Content-Length: ${body.length}\r\n\r\n`);
                socket!.write(body);
                const response = await take(message => message.type === 'response' && message.request_seq === seq);
                assert.strictEqual(response.success, true, JSON.stringify(response));
                return response;
            };
            await request('initialize', { adapterID: 'koh' });
            await request('launch', { stopOnEntry: true, debugInfo: elf });
            await request('configurationDone');
            await take(message => message.event === 'stopped' && message.body.reason === 'entry');
            const source = process.env.KOH_TEST_DAP_SOURCE ?? 'fixture.asm';
            const line = Number(process.env.KOH_TEST_DAP_LINE ?? '5');
            const breakpoint = await request('setBreakpoints', { source: { path: source }, breakpoints: [{ line }] });
            assert.strictEqual(breakpoint.body.breakpoints[0].verified, true);
            await request('continue', { threadId: 1 });
            await take(message => message.event === 'stopped' && message.body.reason === 'breakpoint');
            const stack = await request('stackTrace', { threadId: 1 });
            assert.strictEqual(stack.body.stackFrames[0].line, line);
            assert.strictEqual(stack.body.stackFrames[0].source.path, source);
            await request('stepIn', { threadId: 1 });
            await take(message => message.event === 'stopped' && message.body.reason === 'step');
            const stepped = await request('stackTrace', { threadId: 1 });
            assert.notStrictEqual(stepped.body.stackFrames[0].instructionPointerReference, stack.body.stackFrames[0].instructionPointerReference);
            await request('writeMemory', { memoryReference: '0xC000', data: 'Ql8=' });
            const memory = await request('readMemory', { memoryReference: '0xC000', count: 2 });
            assert.strictEqual(memory.body.data, 'Ql8=');
            await request('disconnect');
            const deadline = Date.now() + 10_000;
            while (child!.exitCode === null) {
                assert.ok(Date.now() < deadline, `desktop survived DAP disconnect\n${diagnostics}`);
                await new Promise(resolve => setTimeout(resolve, 10));
            }
            assert.strictEqual(await exited, 0, diagnostics);
        } catch (error) {
            throw new Error(`${error}\n${diagnostics}`);
        } finally {
            socket?.destroy();
            factory.dispose();
        }
    });
});
