import * as vscode from 'vscode';
import * as path from 'path';
import { spawn as realSpawn, ChildProcess, SpawnOptions } from 'child_process';
import { randomUUID } from 'crypto';
import { Logger } from '../core/Logger';
import { ToolchainResolver } from '../toolchain/ToolchainResolver';
import { executableName } from '../toolchain/paths';
import { waitForDapReady } from './waitForDapReady';

/**
 * Signature matching child_process.spawn — injected via the constructor
 * so integration tests can supply a fake that returns a controllable
 * ChildProcess (EventEmitter with stdout/stderr) without spinning up a
 * real emulator binary.
 */
export type Spawner = (
    command: string,
    args: readonly string[],
    options: SpawnOptions,
) => ChildProcess;

/**
 * Adapter factory for the `koh` debug type. On F5, VS Code asks us to
 * produce a DebugAdapterDescriptor. We:
 *
 *   1. Pick a unique named-pipe name for this session.
 *   2. Spawn Koh.Emulator.App with `--dap=<pipe>` plus the ROM path
 *      from launch.json.
 *   3. Hand VS Code a DebugAdapterNamedPipeServer descriptor pointing
 *      at the same pipe. VS Code connects once the server is up and
 *      drives the DAP conversation directly — we just keep the child
 *      process alive for the session.
 *
 * The emulator itself hosts the debug adapter (see
 * src/Koh.Emulator.App/DapServerHost.cs); this extension only owns
 * launch and teardown.
 *
 * Teardown: the factory tracks every spawned emulator by session id
 * and kills it when VS Code terminates the session. Extension dispose
 * sweeps any stragglers. The emulator *also* self-exits when the DAP
 * pipe disconnects (see DapServerHost.Run), which catches the VS-Code-
 * crashed / extension-host-killed case where `dispose()` never runs.
 */
export class KohDapAdapterFactory implements vscode.DebugAdapterDescriptorFactory, vscode.Disposable {
    private readonly children = new Map<string, ChildProcess>();

    constructor(
        private readonly log: Logger,
        private readonly toolchain: ToolchainResolver,
        private readonly spawner: Spawner = realSpawn,
    ) {}

    async createDebugAdapterDescriptor(session: vscode.DebugSession): Promise<vscode.DebugAdapterDescriptor> {
        const cfg = session.configuration;
        const rom: string | undefined = cfg.program ?? cfg.rom;
        if (!rom) {
            throw new Error('launch config missing "program" (ROM path)');
        }

        const emuPath = this.resolveEmulatorPath(cfg);
        if (!emuPath) {
            throw new Error(
                'No Koh emulator found. Install the toolchain (command: "Koh: Install Toolchain") or set koh.emulator.exePath / launch.emulatorPath.',
            );
        }

        const pipeName = `koh-dap-${randomUUID()}`;
        this.log.info(`spawning emulator: ${emuPath} --dap=${pipeName} ${rom}`);
        const child: ChildProcess = this.spawner(emuPath, [`--dap=${pipeName}`, rom], {
            stdio: ['ignore', 'pipe', 'pipe'],
        });
        this.children.set(session.id, child);

        // Wire the ready-probe BEFORE the log forwarder so we can't
        // miss the `[koh-dap] listening on` line the emulator prints
        // once the pipe server is up. Without waiting, VS Code's DAP
        // client races the ~200ms emulator startup and gets ENOENT
        // on the pipe before Koh.Emulator.App registers the server.
        const ready = waitForDapReady(child, 15_000);

        child.stdout?.on('data', data => this.log.info(`[emu] ${String(data).trimEnd()}`));
        child.stderr?.on('data', data => this.log.info(`[emu] ${String(data).trimEnd()}`));
        // A missing exe shows up as an `error` event with ENOENT before any
        // `exit`; surface it so users see the real cause instead of only the
        // downstream "connect ENOENT \\.\pipe\..." from VS Code's DAP client.
        child.on('error', err => this.log.error(`emulator spawn failed: ${err}`));
        child.on('exit', (code, sig) => {
            this.log.info(`emulator exited code=${code} signal=${sig}`);
            this.children.delete(session.id);
        });

        await ready;
        return new vscode.DebugAdapterNamedPipeServer(this.pipePath(pipeName));
    }

    /**
     * Called from KohExtension's `onDidTerminateDebugSession` subscription
     * when the user stops the session, the emulator crashes, or VS Code
     * tears the session down for any other reason. Without this, the
     * spawned emulator keeps running — on Windows in particular, a child
     * process is not automatically killed when its parent exits.
     */
    terminateSession(sessionId: string): void {
        const child = this.children.get(sessionId);
        if (!child || child.killed || child.exitCode !== null) return;
        this.log.info(`killing emulator for session ${sessionId} (pid=${child.pid})`);
        try { child.kill(); } catch (err) { this.log.warn(`kill failed: ${err}`); }
        // Escalate if the child hasn't exited within a short grace window.
        // Shouldn't normally happen — the emulator self-exits on pipe
        // disconnect — but guards against a hung process ignoring SIGTERM.
        const graceMs = 2000;
        setTimeout(() => {
            if (child.exitCode === null) {
                this.log.warn(`emulator pid=${child.pid} did not exit within ${graceMs}ms; SIGKILL`);
                try { child.kill('SIGKILL'); } catch { /* already gone */ }
            }
        }, graceMs).unref?.();
    }

    dispose(): void {
        for (const [sessionId] of this.children) {
            this.terminateSession(sessionId);
        }
        this.children.clear();
    }


    private pipePath(pipeName: string): string {
        // Windows named-pipe path. .NET's NamedPipeServerStream maps
        // `name` to `\\.\pipe\name`; VS Code expects the same full
        // path. Unix-domain socket paths on Linux/macOS go through a
        // file under /tmp — DAP host and resolver agree on `name`
        // directly there.
        if (process.platform === 'win32') return `\\\\.\\pipe\\${pipeName}`;
        return `/tmp/${pipeName}.sock`;
    }

    private resolveEmulatorPath(cfg: vscode.DebugConfiguration): string | null {
        // Precedence:
        //   1. `emulatorPath` in the launch.json entry — per-session override.
        //   2. `koh.emulator.exePath` user / workspace setting.
        //   3. Emulator shipped with the active toolchain install.
        if (typeof cfg.emulatorPath === 'string' && cfg.emulatorPath.length > 0) {
            return cfg.emulatorPath;
        }
        const setting = vscode.workspace.getConfiguration('koh.emulator').get<string>('exePath');
        if (setting && setting.length > 0) return setting;

        const loc = this.toolchain.resolve();
        if (loc) {
            return path.join(loc.binDir, executableName('Koh.Emulator.App'));
        }
        return null;
    }
}
