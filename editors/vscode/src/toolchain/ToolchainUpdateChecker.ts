import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { GitHubReleaseClient } from './GitHubReleaseClient';
import { compareVersions, ToolchainResolver } from './ToolchainResolver';
import { ToolchainInstaller } from './ToolchainInstaller';

/**
 * On activation, non-blocking: ask GitHub "is there a newer tools-v*
 * release than what I have installed?" and, if so, prompt the user.
 * Respects `koh.toolchain.updateCheck`:
 *   - "prompt" (default): show a non-modal info message with Install / Later.
 *   - "never":  skip entirely.
 *
 * "Later" dismissals are session-scoped (not persisted) — "Later" means
 * "not right now", not "never tell me again". Persistent suppression is
 * what `koh.toolchain.updateCheck = never` is for.
 */
export class ToolchainUpdateChecker {
    private readonly dismissedThisSession = new Set<string>();

    constructor(
        private readonly log: Logger,
        private readonly client: GitHubReleaseClient,
        private readonly resolver: ToolchainResolver,
        private readonly installer: ToolchainInstaller,
    ) {}

    /** Fire-and-forget; exceptions are logged but never propagated to activation. */
    checkInBackground(): void {
        void this.runCheck().catch(err => this.log.warn(`update check failed: ${err}`));
    }

    private async runCheck(): Promise<void> {
        const mode = vscode.workspace.getConfiguration('koh.toolchain').get<string>('updateCheck', 'prompt');
        if (mode === 'never') return;

        const current = this.resolver.resolve();
        if (!current) return; // No toolchain at all — the install prompt handles this, not the updater.

        const latest = await this.client.latestToolRelease();
        if (!latest) {
            this.log.info('no tool releases found yet');
            return;
        }
        if (compareVersions(latest.version, current.version) <= 0) {
            this.log.info(`toolchain up to date (installed ${current.version}, latest ${latest.version})`);
            return;
        }
        if (this.dismissedThisSession.has(latest.version)) return;

        const choice = await vscode.window.showInformationMessage(
            `Koh toolchain ${latest.version} is available (you have ${current.version}).`,
            'Install Update',
            'Later',
        );
        if (choice === 'Install Update') {
            await vscode.window.withProgress(
                { location: vscode.ProgressLocation.Notification, title: 'Updating Koh toolchain', cancellable: false },
                async progress => this.installer.install(latest, progress),
            );
            await vscode.window.showInformationMessage(
                `Koh toolchain ${latest.version} installed. Reload window to use it.`,
                'Reload Window',
            ).then(action => {
                if (action === 'Reload Window') vscode.commands.executeCommand('workbench.action.reloadWindow');
            });
        } else {
            this.dismissedThisSession.add(latest.version);
        }
    }
}
