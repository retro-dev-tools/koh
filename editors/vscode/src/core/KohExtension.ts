import * as vscode from 'vscode';
import { DisposableStore } from './DisposableStore';
import { Logger } from './Logger';
import { LspClientManager } from '../lsp/LspClientManager';
import { KohYamlReader } from '../config/KohYamlReader';
import { BuildTaskProvider } from '../build/BuildTaskProvider';
import { KohDapAdapterFactory } from '../debug/KohDapAdapterFactory';
import { generateConfigCommand, maybePromptGenerateConfig } from '../commands/GenerateConfigCommand';
import { ToolchainResolver } from '../toolchain/ToolchainResolver';
import { GitHubReleaseClient } from '../toolchain/GitHubReleaseClient';
import { ToolchainInstaller } from '../toolchain/ToolchainInstaller';
import { ToolchainUiFlow } from '../toolchain/ToolchainUiFlow';
import { ToolchainUpdateChecker } from '../toolchain/ToolchainUpdateChecker';

/**
 * Extension lifecycle. LSP, tasks, and debug adapter registration all
 * depend on a usable toolchain, so activation pivots on whether one
 * exists:
 *
 *   - Commands (including the install commands) are always registered.
 *   - If no toolchain is resolvable, we prompt the user; the rest of
 *     the feature set boots only after a successful install.
 *   - After boot, a background update check runs without blocking.
 */
export class KohExtension {
    private readonly disposables = new DisposableStore();
    private readonly log: Logger;
    private readonly yamlReader: KohYamlReader;
    private readonly toolchain: ToolchainResolver;
    private readonly releaseClient: GitHubReleaseClient;
    private readonly installer: ToolchainInstaller;
    private readonly uiFlow: ToolchainUiFlow;
    private readonly updateChecker: ToolchainUpdateChecker;
    private readonly lsp: LspClientManager;
    private readonly buildTasks: BuildTaskProvider;

    constructor(private readonly context: vscode.ExtensionContext) {
        this.log = new Logger('Koh');
        this.disposables.add(this.log);
        this.yamlReader = new KohYamlReader(this.log);
        this.toolchain = new ToolchainResolver(this.log);
        this.releaseClient = new GitHubReleaseClient(this.log, 'retro-dev-tools/koh');
        this.installer = new ToolchainInstaller(this.log, this.releaseClient, this.toolchain);
        this.uiFlow = new ToolchainUiFlow(this.log, this.releaseClient, this.toolchain, this.installer);
        this.updateChecker = new ToolchainUpdateChecker(this.log, this.releaseClient, this.toolchain, this.installer);
        this.lsp = new LspClientManager(this.log, this.toolchain);
        this.buildTasks = new BuildTaskProvider(this.log, this.yamlReader, this.toolchain);
    }

    async start(): Promise<void> {
        this.log.info('Koh extension activating...');

        this.registerCommands();

        const hasToolchain = await this.uiFlow.promptInstallIfMissing();
        if (hasToolchain) {
            await this.lsp.start();
            this.disposables.add(this.lsp);
            this.disposables.add(this.buildTasks.register());
            this.disposables.add(vscode.debug.registerDebugAdapterDescriptorFactory(
                'koh',
                new KohDapAdapterFactory(this.log, this.toolchain)));
            // Fire-and-forget update check only once we already have
            // something installed — first-run installs have just
            // grabbed the latest, so polling GH again is pointless.
            this.updateChecker.checkInBackground();
        } else {
            this.log.info('no toolchain available — LSP / build / debug features disabled until installed');
            vscode.window.setStatusBarMessage('Koh: toolchain not installed', 10_000);
        }

        await maybePromptGenerateConfig(this.log);
    }

    private registerCommands(): void {
        this.disposables.add(
            vscode.commands.registerCommand('koh.generateConfig', () => generateConfigCommand(this.log)),
        );
        this.disposables.add(
            vscode.commands.registerCommand('koh.installToolchain', () => this.uiFlow.runInstallLatest()),
        );
        this.disposables.add(
            vscode.commands.registerCommand('koh.updateToolchain', () => this.uiFlow.runInstallLatest()),
        );
        this.disposables.add(
            vscode.commands.registerCommand('koh.showToolchainInfo', () => this.uiFlow.showInfo()),
        );
        this.disposables.add(
            vscode.commands.registerCommand('koh.selectActiveToolchain', () => this.uiFlow.selectActive()),
        );
    }

    async dispose(): Promise<void> {
        this.disposables.dispose();
    }
}
