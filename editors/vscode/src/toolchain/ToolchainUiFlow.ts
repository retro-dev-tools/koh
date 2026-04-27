import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { detectRid, toolchainRoot } from './paths';
import { GitHubReleaseClient } from './GitHubReleaseClient';
import { ToolchainInstaller } from './ToolchainInstaller';
import { ToolchainResolver } from './ToolchainResolver';

/**
 * Glue between the extension lifecycle and the toolchain machinery.
 * Owns the first-run prompt, the command handlers, and the progress
 * UI wrapper — keeping those out of KohExtension.ts so activation
 * stays readable.
 */
export class ToolchainUiFlow {
    constructor(
        private readonly log: Logger,
        private readonly client: GitHubReleaseClient,
        private readonly resolver: ToolchainResolver,
        private readonly installer: ToolchainInstaller,
    ) {}

    /**
     * First-run: if nothing resolves, ask the user to install. Returns
     * true if a toolchain is available after the prompt (caller can
     * then start the LSP), false if the user declined or the install
     * failed.
     */
    async promptInstallIfMissing(): Promise<boolean> {
        if (this.resolver.resolve()) return true;

        const rid = detectRid();
        if (!rid) {
            await vscode.window.showErrorMessage(
                `Koh doesn't publish a toolchain for ${process.platform}/${process.arch}. Build from source and set koh.toolchainPath.`,
            );
            return false;
        }

        const choice = await vscode.window.showInformationMessage(
            'Koh toolchain is not installed. Download and install the latest version?',
            { modal: true, detail: `Installs to ${toolchainRoot()} (no admin required).` },
            'Install',
            'Open Releases Page',
        );
        if (choice === 'Open Releases Page') {
            await vscode.env.openExternal(vscode.Uri.parse('https://github.com/retro-dev-tools/koh/releases'));
            return false;
        }
        if (choice !== 'Install') return false;

        return this.runInstallLatest();
    }

    /** `koh.installToolchain` / `koh.updateToolchain` — same flow, different copy. */
    async runInstallLatest(): Promise<boolean> {
        try {
            const latest = await this.client.latestToolRelease();
            if (!latest) {
                await vscode.window.showWarningMessage('No Koh toolchain releases found yet.');
                return false;
            }
            await vscode.window.withProgress(
                { location: vscode.ProgressLocation.Notification, title: `Installing Koh toolchain ${latest.version}`, cancellable: false },
                async progress => this.installer.install(latest, progress),
            );
            await vscode.window.showInformationMessage(
                `Koh toolchain ${latest.version} installed. Reload window to start the language server.`,
                'Reload Window',
            ).then(action => {
                if (action === 'Reload Window') vscode.commands.executeCommand('workbench.action.reloadWindow');
            });
            return true;
        } catch (err) {
            this.log.error(`toolchain install failed: ${err}`);
            await vscode.window.showErrorMessage(`Koh toolchain install failed: ${err}`);
            return false;
        }
    }

    /** `koh.showToolchainInfo` — diagnostic dump, easier than asking users to copy the log. */
    async showInfo(): Promise<void> {
        const loc = this.resolver.resolve();
        const managed = this.resolver.listManaged();
        const pointer = this.resolver.readCurrentPointer();

        const lines = [
            `Toolchain root: ${toolchainRoot()}`,
            `Current pointer: ${pointer ?? '(none)'}`,
            loc
                ? `Active: ${loc.version} (source=${loc.source}, bin=${loc.binDir})`
                : `Active: none resolved`,
            '',
            `Managed installs:`,
            ...(managed.length === 0 ? ['  (none)'] : managed.map(m => `  - ${m.version}`)),
        ];
        const doc = await vscode.workspace.openTextDocument({ content: lines.join('\n'), language: 'plaintext' });
        await vscode.window.showTextDocument(doc, { preview: true });
    }

    /** `koh.selectActiveToolchain` — pick one of the managed installs to mark as `current`. */
    async selectActive(): Promise<void> {
        const managed = this.resolver.listManaged();
        if (managed.length === 0) {
            await vscode.window.showInformationMessage('No managed toolchain installs found.');
            return;
        }
        const pointer = this.resolver.readCurrentPointer();
        const items = managed.map(m => ({
            label: m.version,
            description: m.version === pointer ? '(current)' : '',
            detail: m.meta ? `installed ${m.meta.installedAt.slice(0, 10)}` : '',
            version: m.version,
        }));
        const pick = await vscode.window.showQuickPick(items, { placeHolder: 'Select active Koh toolchain' });
        if (!pick) return;
        this.resolver.writeCurrentPointer(pick.version);
        await vscode.window.showInformationMessage(
            `Active toolchain set to ${pick.version}. Reload window to apply.`,
            'Reload Window',
        ).then(action => {
            if (action === 'Reload Window') vscode.commands.executeCommand('workbench.action.reloadWindow');
        });
    }
}
