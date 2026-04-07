import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
} from 'vscode-languageclient/node';
import * as path from 'path';
import * as fs from 'fs';

let client: LanguageClient;
let log: vscode.LogOutputChannel;

function findServer(): string | null {
    // 1. Check user setting
    const configPath = vscode.workspace.getConfiguration('koh').get<string>('serverPath');
    log.info(`koh.serverPath = "${configPath || ''}"`);
    if (configPath && fs.existsSync(configPath)) {
        log.info(`Found server at configured path: ${configPath}`);
        return configPath;
    }

    // 2. Check bundled server next to extension
    log.info(`__dirname = ${__dirname}`);
    const bundledCandidates = [
        path.join(__dirname, '..', 'server', 'koh-lsp'),
        path.join(__dirname, '..', 'server', 'koh-lsp.exe'),
    ];
    for (const candidate of bundledCandidates) {
        const exists = fs.existsSync(candidate);
        log.info(`${candidate} → ${exists ? 'FOUND' : 'not found'}`);
        if (exists) return candidate;
    }

    // 3. Not found
    log.warn('No server binary found');
    return null;
}

export async function activate(context: vscode.ExtensionContext) {
    log = vscode.window.createOutputChannel('Koh', { log: true });
    log.info('Koh extension activating...');

    const serverPath = findServer();

    if (!serverPath) {
        const msg = 'Koh language server (koh-lsp) not found. Set koh.serverPath in settings, or build with: dotnet publish src/Koh.Lsp -c Release -o editors/vscode/server';
        log.error(msg);
        log.show(true);
        vscode.window.showWarningMessage(msg);
        return;
    }

    log.info(`Using server: ${serverPath}`);

    const serverOptions: ServerOptions = {
        command: serverPath,
        args: [],
    };

    const clientOptions: LanguageClientOptions = {
        documentSelector: [
            { scheme: 'file', language: 'koh-asm' },
            { scheme: 'untitled', language: 'koh-asm' },
        ],
        outputChannel: log,
    };

    client = new LanguageClient(
        'koh-lsp',
        'Koh Language Server',
        serverOptions,
        clientOptions,
    );

    log.info('Starting language client...');
    try {
        await client.start();
        log.info('Language client started successfully');
    } catch (e) {
        log.error(`Failed to start: ${e}`);
        log.show(true);
    }
    context.subscriptions.push(client);
}

export function deactivate(): Thenable<void> | undefined {
    log?.info('Deactivating...');
    return undefined;
}
