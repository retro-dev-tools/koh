import * as path from 'path';
import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
} from 'vscode-languageclient/node';
import { Logger } from '../core/Logger';
import { ToolchainResolver } from '../toolchain/ToolchainResolver';
import { executableName } from '../toolchain/paths';

export class LspClientManager implements vscode.Disposable {
    private client: LanguageClient | undefined;

    constructor(
        private readonly log: Logger,
        private readonly toolchain: ToolchainResolver,
    ) {}

    async start(): Promise<void> {
        const loc = this.toolchain.resolve();
        if (!loc) {
            // Activation flow should have prompted already; log and bail.
            this.log.warn('no toolchain resolved — LSP not starting');
            return;
        }
        const serverPath = path.join(loc.binDir, executableName('koh-lsp'));
        this.log.info(`using koh-lsp (${loc.source}, ${loc.version}): ${serverPath}`);

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
