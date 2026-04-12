import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { EmulatorPanel } from './EmulatorPanel';
import { BlazorAssetLoader } from './BlazorAssetLoader';

export class EmulatorPanelHost {
    private activePanels = new Map<string, EmulatorPanel>();

    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly log: Logger
    ) {}

    openForSession(session: vscode.DebugSession): EmulatorPanel {
        const existing = this.activePanels.get(session.id);
        if (existing) return existing;

        const assetLoader = new BlazorAssetLoader(this.context, this.log);
        if (!assetLoader.bundledAssetsPresent()) {
            const devHost = vscode.workspace.getConfiguration('koh').get<string>('emulator.devHostUrl');
            if (!devHost) {
                vscode.window.showErrorMessage(
                    'Koh emulator assets not found. Run: dotnet publish src/Koh.Emulator.App -c Release and copy wwwroot to editors/vscode/dist/emulator-app'
                );
            }
        }

        const panel = new EmulatorPanel(session, this.log, this.context, assetLoader);
        this.activePanels.set(session.id, panel);
        panel.onDidDispose(() => this.activePanels.delete(session.id));
        return panel;
    }
}
