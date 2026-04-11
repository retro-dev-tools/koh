import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { Logger } from '../core/Logger';

export interface BlazorAssetSource {
    baseUri: vscode.Uri | string;
    cspSources: string[];
}

export class BlazorAssetLoader {
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly log: Logger
    ) {}

    resolve(webview: vscode.Webview): BlazorAssetSource {
        const devHostUrl = vscode.workspace.getConfiguration('koh').get<string>('emulator.devHostUrl');

        if (devHostUrl && this.isDevHostPermitted(devHostUrl)) {
            this.log.info(`Using dev-host Blazor assets at ${devHostUrl}`);
            return {
                baseUri: devHostUrl,
                cspSources: [devHostUrl],
            };
        }

        // Bundled assets path.
        const bundledDir = vscode.Uri.joinPath(this.context.extensionUri, 'dist', 'emulator-app');
        const baseUri = webview.asWebviewUri(bundledDir);
        return {
            baseUri,
            cspSources: [],
        };
    }

    private isDevHostPermitted(url: string): boolean {
        // Rule 1: extension mode must be Development.
        if (this.context.extensionMode !== vscode.ExtensionMode.Development) {
            this.log.warn('koh.emulator.devHostUrl set but extension is not in Development mode; ignoring.');
            return false;
        }

        // Rule 2: whitelist localhost / 127.0.0.1 with explicit port.
        try {
            const parsed = new URL(url);
            if (parsed.protocol !== 'http:') {
                this.log.warn(`devHostUrl rejected: non-http protocol (${parsed.protocol})`);
                return false;
            }
            if (parsed.hostname !== 'localhost' && parsed.hostname !== '127.0.0.1') {
                this.log.warn(`devHostUrl rejected: host must be localhost or 127.0.0.1 (${parsed.hostname})`);
                return false;
            }
            const port = parseInt(parsed.port, 10);
            if (!(port >= 1024 && port <= 65535)) {
                this.log.warn(`devHostUrl rejected: invalid port ${parsed.port}`);
                return false;
            }
            return true;
        } catch {
            this.log.warn(`devHostUrl rejected: not a valid URL (${url})`);
            return false;
        }
    }

    bundledAssetsPresent(): boolean {
        const indexHtml = path.join(this.context.extensionPath, 'dist', 'emulator-app', 'index.html');
        return fs.existsSync(indexHtml);
    }
}
