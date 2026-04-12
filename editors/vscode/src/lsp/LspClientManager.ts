import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
} from 'vscode-languageclient/node';
import { Logger } from '../core/Logger';
import { resolveLspServerPath } from './serverPathResolver';

export class LspClientManager implements vscode.Disposable {
    private client: LanguageClient | undefined;

    constructor(private readonly log: Logger) {}

    async start(): Promise<void> {
        const serverPath = resolveLspServerPath(this.log);
        if (!serverPath) {
            const msg = 'Koh language server (koh-lsp) not found. Set koh.serverPath in settings, or build with: dotnet publish src/Koh.Lsp -c Release -o editors/vscode/server';
            this.log.error(msg);
            this.log.show();
            vscode.window.showWarningMessage(msg);
            return;
        }

        this.log.info(`Using server: ${serverPath}`);

        const serverOptions: ServerOptions = {
            command: serverPath,
            args: [],
        };

        const clientOptions: LanguageClientOptions = {
            documentSelector: [
                { scheme: 'file', language: 'koh-asm' },
                { scheme: 'untitled', language: 'koh-asm' },
            ],
            outputChannel: this.log.outputChannel,
        };

        this.client = new LanguageClient('koh-lsp', 'Koh Language Server', serverOptions, clientOptions);

        this.log.info('Starting language client...');
        try {
            await this.client.start();
            this.log.info('Language client started successfully');
        } catch (e) {
            this.log.error(`Failed to start: ${e}`);
            this.log.show();
        }
    }

    async dispose(): Promise<void> {
        await this.client?.stop();
    }
}
