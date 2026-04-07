import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
} from 'vscode-languageclient/node';
import * as path from 'path';
import * as fs from 'fs';

let client: LanguageClient;
let log: vscode.OutputChannel;

function findServer(): string | null {
    // 1. Check user setting
    const configPath = vscode.workspace.getConfiguration('koh').get<string>('serverPath');
    log.appendLine(`[config] koh.serverPath = "${configPath || ''}"`);
    if (configPath && fs.existsSync(configPath)) {
        log.appendLine(`[config] Found server at configured path: ${configPath}`);
        return configPath;
    }

    // 2. Check bundled server next to extension
    log.appendLine(`[search] __dirname = ${__dirname}`);
    const bundledCandidates = [
        path.join(__dirname, '..', 'server', 'koh-lsp'),
        path.join(__dirname, '..', 'server', 'koh-lsp.exe'),
    ];
    for (const candidate of bundledCandidates) {
        const exists = fs.existsSync(candidate);
        log.appendLine(`[search] ${candidate} → ${exists ? 'FOUND' : 'not found'}`);
        if (exists) return candidate;
    }

    // 3. Not found
    log.appendLine('[search] No server binary found');
    return null;
}

export async function activate(context: vscode.ExtensionContext) {
    log = vscode.window.createOutputChannel('Koh');
    log.appendLine('Koh extension activating...');

    const serverPath = findServer();

    if (!serverPath) {
        const msg = 'Koh language server (koh-lsp) not found. Set koh.serverPath in settings, or build with: dotnet publish src/Koh.Lsp -c Release -o editors/vscode/server';
        log.appendLine(`[error] ${msg}`);
        log.show(true);
        vscode.window.showWarningMessage(msg);
        return;
    }

    log.appendLine(`[server] Using: ${serverPath}`);

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

    log.appendLine('[client] Starting language client...');
    try {
        await client.start();
        log.appendLine('[client] Language client started successfully');
    } catch (e) {
        log.appendLine(`[client] Failed to start: ${e}`);
        log.show(true);
    }
    context.subscriptions.push(client);
}

export function deactivate(): Thenable<void> | undefined {
    log?.appendLine('[client] Deactivating...');
    return undefined;
}
