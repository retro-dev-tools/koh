import * as path from 'path';
import * as vscode from 'vscode';
import { ChildProcess, spawn } from 'child_process';
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
    private serverClosed: Promise<void> | undefined;
    private server: ChildProcess | undefined;
    private configSaves: vscode.Disposable | undefined;

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

        const serverOptions: ServerOptions = async () => {
            const child = spawn(serverPath, [], {
                cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath,
                windowsHide: true,
            });
            this.serverClosed = new Promise(resolve => child.once('close', () => resolve()));
            this.server = child;
            await new Promise<void>((resolve, reject) => {
                child.once('spawn', resolve);
                child.once('error', reject);
            });
            return child;
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
            const client = this.client;
            this.configSaves = vscode.workspace.onDidSaveTextDocument(document => {
                const root = vscode.workspace.workspaceFolders?.[0]?.uri;
                if (root && document.uri.scheme === 'file' && path.relative(root.fsPath, document.uri.fsPath).toLowerCase() === 'koh.yaml') {
                    void client.sendNotification('textDocument/didSave', { textDocument: { uri: document.uri.toString() } })
                        .catch(error => this.log.error(`Config reload failed: ${error}`));
                }
            });
            this.log.info('Language client started successfully');
        } catch (e) {
            this.client = undefined;
            this.server?.kill();
            this.log.error(`Failed to start: ${e}`);
            this.log.show();
        }
    }

    async dispose(): Promise<void> {
        this.configSaves?.dispose();
        this.configSaves = undefined;
        const client = this.client;
        this.client = undefined;
        try {
            await client?.stop();
        } catch (error) {
            this.server?.kill();
            throw error;
        } finally {
            await this.serverClosed;
            this.serverClosed = undefined;
            this.server = undefined;
        }
    }
}
