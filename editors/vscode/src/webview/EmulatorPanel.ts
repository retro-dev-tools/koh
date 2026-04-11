import * as vscode from 'vscode';
import * as path from 'path';
import { Logger } from '../core/Logger';
import { BlazorAssetLoader } from './BlazorAssetLoader';
import { buildEmulatorHtml } from './EmulatorHtml';
import { ExtensionToWebviewMessage, WebviewToExtensionMessage } from './messages';

export class EmulatorPanel implements vscode.Disposable {
    private readonly panel: vscode.WebviewPanel;
    private readonly messageEmitter = new vscode.EventEmitter<WebviewToExtensionMessage>();
    readonly onMessageFromWebview = this.messageEmitter.event;

    constructor(
        private readonly session: vscode.DebugSession,
        private readonly log: Logger,
        private readonly context: vscode.ExtensionContext,
        private readonly assetLoader: BlazorAssetLoader
    ) {
        this.panel = vscode.window.createWebviewPanel(
            'kohEmulator',
            `Koh Emulator — ${session.name}`,
            vscode.ViewColumn.Beside,
            {
                enableScripts: true,
                retainContextWhenHidden: true,
                localResourceRoots: [
                    vscode.Uri.joinPath(this.context.extensionUri, 'dist', 'emulator-app'),
                ],
            }
        );

        const assets = this.assetLoader.resolve(this.panel.webview);
        this.panel.webview.html = buildEmulatorHtml(this.panel.webview, assets);

        this.panel.webview.onDidReceiveMessage((msg: WebviewToExtensionMessage) => {
            this.messageEmitter.fire(msg);
        });

        this.panel.onDidDispose(() => this.dispose());
    }

    postToWebview(msg: ExtensionToWebviewMessage | unknown): void {
        this.panel.webview.postMessage(msg);
    }

    dispose(): void {
        this.messageEmitter.dispose();
        this.panel.dispose();
    }
}
