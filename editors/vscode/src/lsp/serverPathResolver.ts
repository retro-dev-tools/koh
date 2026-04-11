import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { Logger } from '../core/Logger';

export function resolveLspServerPath(log: Logger): string | null {
    // 1. User setting
    const configPath = vscode.workspace.getConfiguration('koh').get<string>('serverPath');
    log.info(`koh.serverPath = "${configPath || ''}"`);
    if (configPath && fs.existsSync(configPath)) {
        log.info(`Found server at configured path: ${configPath}`);
        return configPath;
    }

    // 2. Bundled server next to extension
    const bundled = [
        path.join(__dirname, '..', '..', 'server', 'koh-lsp'),
        path.join(__dirname, '..', '..', 'server', 'koh-lsp.exe'),
    ];
    for (const candidate of bundled) {
        const exists = fs.existsSync(candidate);
        log.info(`${candidate} → ${exists ? 'FOUND' : 'not found'}`);
        if (exists) return candidate;
    }

    log.warn('No server binary found');
    return null;
}
