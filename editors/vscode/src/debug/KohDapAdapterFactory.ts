import * as vscode from 'vscode';
import * as path from 'path';
import { spawn, ChildProcess } from 'child_process';
import { Logger } from '../core/Logger';
import { randomUUID } from 'crypto';

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
    constructor(private readonly log: Logger) {}

    createDebugAdapterDescriptor(session: vscode.DebugSession): vscode.ProviderResult<vscode.DebugAdapterDescriptor> {
        const cfg = session.configuration;
        const rom: string | undefined = cfg.program ?? cfg.rom;
        if (!rom) {
            throw new Error('launch config missing "program" (ROM path)');
        }

        const pipeName = `koh-dap-${randomUUID()}`;
        const emuPath = this.resolveEmulatorPath(cfg);
        this.log.info(`spawning emulator: ${emuPath} --dap=${pipeName} ${rom}`);
        const child: ChildProcess = spawn(emuPath, [`--dap=${pipeName}`, rom], {
            stdio: ['ignore', 'pipe', 'pipe'],
        });
        child.stdout?.on('data', data => this.log.info(`[emu] ${String(data).trimEnd()}`));
        child.stderr?.on('data', data => this.log.info(`[emu] ${String(data).trimEnd()}`));
        child.on('exit', (code, sig) => this.log.info(`emulator exited code=${code} signal=${sig}`));

        // VS Code sometimes tries to connect before the server pipe
        // is listening. The DebugAdapterNamedPipeServer descriptor
        // retries internally, but the first few attempts can error in
        // the log; nothing to do about that here beyond waiting.
        return new vscode.DebugAdapterNamedPipeServer(this.pipePath(pipeName));
    }

    private pipePath(pipeName: string): string {
        // Windows named-pipe path. .NET's NamedPipeServerStream maps
        // `name` to `\\.\pipe\name`; VS Code expects the same full
        // path. Cross-platform support (Unix-domain sockets with a
        // different path convention) lands with the Linux / macOS
        // packaging work.
        return `\\\\.\\pipe\\${pipeName}`;
    }

    private resolveEmulatorPath(cfg: vscode.DebugConfiguration): string {
        // Precedence:
        //   1. `emulatorPath` in the launch.json entry — per-session override.
        //   2. `koh.emulator.exePath` user / workspace setting — survives
        //      launch-config edits and is what scripts/install-vscode-
        //      extension.* tells the user to point at the repo's
        //      Release publish.
        //   3. A path relative to the extension (for dev-host F5 runs
        //      where __dirname is in the repo, not an installed copy).
        if (typeof cfg.emulatorPath === 'string' && cfg.emulatorPath.length > 0) {
            return cfg.emulatorPath;
        }
        const setting = vscode.workspace.getConfiguration('koh.emulator').get<string>('exePath');
        if (setting && setting.length > 0) return setting;

        const repoRoot = path.resolve(__dirname, '..', '..', '..', '..');
        const exe = process.platform === 'win32' ? 'Koh.Emulator.App.exe' : 'Koh.Emulator.App';
        return path.join(repoRoot, 'src', 'Koh.Emulator.App', 'bin', 'Release', 'net10.0', 'win-x64', 'publish', exe);
    }
}
