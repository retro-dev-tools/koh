import * as vscode from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
} from 'vscode-languageclient/node';
import * as path from 'path';
import * as fs from 'fs';

// Client lifecycle managed via context.subscriptions — no explicit deactivate needed.

function findServer(): string | null {
    // 1. Check user setting
    const configPath = vscode.workspace.getConfiguration('koh').get<string>('serverPath');
    if (configPath && fs.existsSync(configPath)) return configPath;

    // 2. Check bundled server next to extension
    const bundledCandidates = [
        path.join(__dirname, '..', 'server', 'koh-lsp'),
        path.join(__dirname, '..', 'server', 'koh-lsp.exe'),
    ];
    for (const candidate of bundledCandidates) {
        if (fs.existsSync(candidate)) return candidate;
    }

    // 3. Not found — let user know
    return null;
}

export async function activate(context: vscode.ExtensionContext) {
    const serverPath = findServer();

    if (!serverPath) {
        vscode.window.showWarningMessage(
            'Koh language server (koh-lsp) not found. Set koh.serverPath in settings, or install koh-lsp on your PATH.'
        );
        return;
    }

    const serverOptions: ServerOptions = {
        command: serverPath,
        args: [],
    };

    const clientOptions: LanguageClientOptions = {
        documentSelector: [
            { scheme: 'file', language: 'koh-asm' },
            { scheme: 'untitled', language: 'koh-asm' },
        ],
    };

    client = new LanguageClient(
        'koh-lsp',
        'Koh Language Server',
        serverOptions,
        clientOptions,
    );

    await client.start();
    context.subscriptions.push(client);
}

export function deactivate(): Thenable<void> | undefined {
    // Client stop is handled by context.subscriptions disposal
    return undefined;
}
