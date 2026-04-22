import * as vscode from 'vscode';
import * as path from 'path';
import { spawn, ChildProcess } from 'child_process';
import { randomUUID } from 'crypto';
import { Logger } from '../core/Logger';
import { ToolchainResolver } from '../toolchain/ToolchainResolver';
import { executableName } from '../toolchain/paths';
import { waitForDapReady } from './waitForDapReady';

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
 */
export class KohDapAdapterFactory implements vscode.DebugAdapterDescriptorFactory {
    constructor(
        private readonly log: Logger,
        private readonly toolchain: ToolchainResolver,
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
        const child: ChildProcess = spawn(emuPath, [`--dap=${pipeName}`, rom], {
            stdio: ['ignore', 'pipe', 'pipe'],
        });

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
        child.on('exit', (code, sig) => this.log.info(`emulator exited code=${code} signal=${sig}`));

        await ready;
        return new vscode.DebugAdapterNamedPipeServer(this.pipePath(pipeName));
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
