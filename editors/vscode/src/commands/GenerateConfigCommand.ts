import * as vscode from 'vscode';
import * as path from 'path';
import { Logger } from '../core/Logger';

/** Well-known entrypoint filenames, ordered by confidence. */
const ENTRYPOINT_NAMES = ['main.asm', 'game.asm', 'app.asm', 'index.asm'];

/** Maximum number of entrypoints to put in a generated koh.yaml. */
const MAX_GENERATED_ENTRYPOINTS = 5;

/** Workspace folders we've already prompted about koh.yaml this session. */
export const promptedFolders = new Set<string>();

/**
 * Scan the workspace folder for .asm files and try to guess which ones are
 * root entrypoints.
 */
async function guessEntrypoints(folder: vscode.Uri): Promise<string[]> {
    const pattern = new vscode.RelativePattern(folder, '**/*.asm');
    const uris = await vscode.workspace.findFiles(pattern);

    if (uris.length === 0) return [];
    if (uris.length === 1) {
        return [vscode.workspace.asRelativePath(uris[0], false)];
    }

    const relPaths = new Map<string, string>();
    const suffixIndex = new Map<string, string[]>();
    for (const uri of uris) {
        const rel = vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/');
        relPaths.set(rel.toLowerCase(), rel);
        const lower = rel.toLowerCase();
        const parts = lower.split('/');
        for (let i = 0; i < parts.length; i++) {
            const suffix = parts.slice(i).join('/');
            if (!suffixIndex.has(suffix)) suffixIndex.set(suffix, []);
            suffixIndex.get(suffix)!.push(rel);
        }
    }

    function resolveInclude(raw: string, includerRel: string): string | undefined {
        const rawLower = raw.toLowerCase();
        const includerDir = path.posix.dirname(includerRel);

        if (relPaths.has(rawLower)) return relPaths.get(rawLower)!;

        const fromFile = path.posix.normalize(path.posix.join(includerDir, raw));
        if (relPaths.has(fromFile.toLowerCase())) return relPaths.get(fromFile.toLowerCase())!;

        let normalized = path.posix.normalize(raw);
        while (normalized.startsWith('../')) normalized = normalized.slice(3);
        const matches = suffixIndex.get(normalized.toLowerCase());
        if (matches && matches.length === 1) return matches[0];

        return undefined;
    }

    const includedFiles = new Set<string>();
    const includeCount = new Map<string, number>();
    for (const uri of uris) {
        try {
            const bytes = await vscode.workspace.fs.readFile(uri);
            const text = Buffer.from(bytes).toString('utf-8');
            const includerRel = vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/');
            let count = 0;
            const regex = /^\s*(?:INCLUDE|INCBIN)\s+"([^"]+)"/gmi;
            let m: RegExpExecArray | null;
            while ((m = regex.exec(text)) !== null) {
                const raw = m[1].replace(/\\/g, '/');
                if (!raw.endsWith('.asm') && !raw.endsWith('.inc')) continue;
                count++;
                const resolved = resolveInclude(raw, includerRel);
                if (resolved) includedFiles.add(resolved);
            }
            includeCount.set(includerRel, count);
        } catch {
            // Unreadable file — skip
        }
    }

    const roots: string[] = [];
    for (const uri of uris) {
        const rel = vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/');
        if (!includedFiles.has(rel)) {
            roots.push(rel);
        }
    }

    if (roots.length === 0) return [];

    const score = (r: string) => entrypointScore(r, includeCount.get(r) ?? 0);
    roots.sort((a, b) => {
        const diff = score(b) - score(a);
        return diff !== 0 ? diff : a.localeCompare(b);
    });

    const viable = roots.filter(r => score(r) > 0);
    if (viable.length > 0) {
        return viable.slice(0, MAX_GENERATED_ENTRYPOINTS);
    }

    if (roots.length > MAX_GENERATED_ENTRYPOINTS) {
        return roots.slice(0, MAX_GENERATED_ENTRYPOINTS);
    }

    return roots;
}

function entrypointScore(relPath: string, includeCount: number): number {
    const name = path.basename(relPath).toLowerCase();
    const dir = path.dirname(relPath).replace(/\\/g, '/');

    let score = 0;

    if (includeCount > 0) score += 20;

    const idx = ENTRYPOINT_NAMES.indexOf(name);
    if (idx !== -1) score += 10 - idx;

    if (dir === '.' || dir === '') score += 5;
    else if (dir === 'src') score += 3;

    if (/^(tools|test|tests|build)(\/|$)/i.test(relPath)) score -= 50;

    return score;
}

function buildYaml(entrypoints: string[]): string {
    if (entrypoints.length === 0) {
        return [
            'version: 1',
            'projects:',
            '  - name: game',
            '    entrypoint: src/main.asm',
            '',
        ].join('\n');
    }

    const lines = ['version: 1', 'projects:'];
    for (const entrypoint of entrypoints) {
        const ep = entrypoint.replace(/\\/g, '/');
        const name = path.basename(ep, path.extname(ep));
        lines.push(`  - name: ${name}`);
        lines.push(`    entrypoint: ${ep}`);
    }
    lines.push('');
    return lines.join('\n');
}

export async function generateConfigForFolder(folder: vscode.WorkspaceFolder, log: Logger): Promise<void> {
    const configUri = vscode.Uri.joinPath(folder.uri, 'koh.yaml');

    try {
        await vscode.workspace.fs.stat(configUri);
        const overwrite = await vscode.window.showWarningMessage(
            'koh.yaml already exists in this workspace. Overwrite?',
            'Overwrite',
            'Cancel',
        );
        if (overwrite !== 'Overwrite') return;
    } catch {
        // File doesn't exist — good
    }

    const entrypoints = await guessEntrypoints(folder.uri);
    const yaml = buildYaml(entrypoints);

    await vscode.workspace.fs.writeFile(configUri, Buffer.from(yaml, 'utf-8'));
    promptedFolders.add(folder.uri.toString());

    const doc = await vscode.workspace.openTextDocument(configUri);
    await vscode.window.showTextDocument(doc);

    log.info(`Generated koh.yaml in ${folder.uri.fsPath}`);
}

export async function generateConfigCommand(log: Logger): Promise<void> {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders || folders.length === 0) {
        vscode.window.showWarningMessage('No workspace folder is open.');
        return;
    }

    let folder: vscode.WorkspaceFolder;
    if (folders.length === 1) {
        folder = folders[0];
    } else {
        const picked = await vscode.window.showWorkspaceFolderPick({
            placeHolder: 'Select workspace folder for koh.yaml',
        });
        if (!picked) return;
        folder = picked;
    }

    await generateConfigForFolder(folder, log);
}

export async function maybePromptGenerateConfig(log: Logger): Promise<void> {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders) return;

    for (const folder of folders) {
        const key = folder.uri.toString();
        if (promptedFolders.has(key)) continue;

        const configUri = vscode.Uri.joinPath(folder.uri, 'koh.yaml');
        try {
            await vscode.workspace.fs.stat(configUri);
            promptedFolders.add(key);
            continue;
        } catch {
            // No koh.yaml — check if there are .asm files (heuristic mode)
        }

        const pattern = new vscode.RelativePattern(folder, '**/*.asm');
        const asmFiles = await vscode.workspace.findFiles(pattern, undefined, 1);
        if (asmFiles.length === 0) {
            promptedFolders.add(key);
            continue;
        }

        promptedFolders.add(key);

        const choice = await vscode.window.showInformationMessage(
            'No koh.yaml found. Generate a project config for better LSP support?',
            'Generate',
            'Not now',
        );
        if (choice === 'Generate') {
            await generateConfigForFolder(folder, log);
        }
    }
}
