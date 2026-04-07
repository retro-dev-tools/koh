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

/** Workspace folders we've already prompted about koh.yaml this session. */
const promptedFolders = new Set<string>();

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

// ---------------------------------------------------------------------------
// koh.yaml generation
// ---------------------------------------------------------------------------

/** Well-known entrypoint filenames, ordered by confidence. */
const ENTRYPOINT_NAMES = ['main.asm', 'game.asm', 'app.asm', 'index.asm'];

/** Maximum number of entrypoints to put in a generated koh.yaml. */
const MAX_GENERATED_ENTRYPOINTS = 5;

/**
 * Scan the workspace folder for .asm files and try to guess which ones are
 * root entrypoints (i.e. not included by other files).
 *
 * INCLUDE paths in RGBDS are resolved relative to CWD (workspace root) first,
 * then relative to the including file's directory. We try both when matching.
 */
async function guessEntrypoints(folder: vscode.Uri): Promise<string[]> {
    const pattern = new vscode.RelativePattern(folder, '**/*.asm');
    const uris = await vscode.workspace.findFiles(pattern);

    if (uris.length === 0) return [];
    if (uris.length === 1) {
        return [vscode.workspace.asRelativePath(uris[0], false)];
    }

    // Build a set of all known relative paths for quick lookup.
    const relPaths = new Map<string, string>(); // normalized → original relative
    for (const uri of uris) {
        const rel = vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/');
        relPaths.set(rel.toLowerCase(), rel);
    }

    // Collect all INCLUDE/INCBIN references, resolving relative to both
    // workspace root and including file's directory.
    const includedFiles = new Set<string>();
    for (const uri of uris) {
        try {
            const bytes = await vscode.workspace.fs.readFile(uri);
            const text = Buffer.from(bytes).toString('utf-8');
            const includerRel = vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/');
            const includerDir = path.posix.dirname(includerRel);
            const regex = /^\s*(?:INCLUDE|INCBIN)\s+"([^"]+)"/gmi;
            let m: RegExpExecArray | null;
            while ((m = regex.exec(text)) !== null) {
                const raw = m[1].replace(/\\/g, '/');

                // Try 1: raw path as-is (relative to workspace root / CWD)
                if (relPaths.has(raw.toLowerCase())) {
                    includedFiles.add(relPaths.get(raw.toLowerCase())!);
                }

                // Try 2: resolved relative to the including file's directory
                const resolved = path.posix.normalize(path.posix.join(includerDir, raw));
                if (relPaths.has(resolved.toLowerCase())) {
                    includedFiles.add(relPaths.get(resolved.toLowerCase())!);
                }
            }
        } catch {
            // Unreadable file — skip
        }
    }

    // Filter to roots (not included by anyone else).
    const roots: string[] = [];
    for (const uri of uris) {
        const rel = vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/');
        if (!includedFiles.has(rel)) {
            roots.push(rel);
        }
    }

    if (roots.length === 0) {
        // Everything is included by something — return empty (template will be used)
        return [];
    }

    // Sort by heuristic confidence: well-known names first, then root/src files, then alphabetical.
    roots.sort((a, b) => {
        const scoreA = entrypointScore(a);
        const scoreB = entrypointScore(b);
        if (scoreA !== scoreB) return scoreB - scoreA;
        return a.localeCompare(b);
    });

    // If too many roots found, the heuristic isn't working well for this project.
    // Only return the highest-confidence ones.
    if (roots.length > MAX_GENERATED_ENTRYPOINTS) {
        const highConfidence = roots.filter(r => entrypointScore(r) > 0);
        if (highConfidence.length > 0 && highConfidence.length <= MAX_GENERATED_ENTRYPOINTS) {
            return highConfidence;
        }
        // Still too many or none with confidence — return just the top few
        return roots.slice(0, MAX_GENERATED_ENTRYPOINTS);
    }

    return roots;
}

function entrypointScore(relPath: string): number {
    const name = path.basename(relPath).toLowerCase();
    const dir = path.dirname(relPath).replace(/\\/g, '/');

    let score = 0;

    // Well-known filename bonus
    const idx = ENTRYPOINT_NAMES.indexOf(name);
    if (idx !== -1) score += 10 - idx;

    // Root or src/ directory bonus
    if (dir === '.' || dir === '') score += 5;
    else if (dir === 'src') score += 3;

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
    for (let i = 0; i < entrypoints.length; i++) {
        const ep = entrypoints[i].replace(/\\/g, '/');
        const name = path.basename(ep, path.extname(ep));
        lines.push(`  - name: ${name}`);
        lines.push(`    entrypoint: ${ep}`);
    }
    lines.push('');
    return lines.join('\n');
}

async function generateConfigForFolder(folder: vscode.WorkspaceFolder): Promise<void> {
    const configUri = vscode.Uri.joinPath(folder.uri, 'koh.yaml');

    // Warn if file already exists
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

async function generateConfigCommand(): Promise<void> {
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

    await generateConfigForFolder(folder);
}

// ---------------------------------------------------------------------------
// Prompt to generate koh.yaml (heuristic mode detection)
// ---------------------------------------------------------------------------

async function maybePromptGenerateConfig(): Promise<void> {
    const folders = vscode.workspace.workspaceFolders;
    if (!folders) return;

    for (const folder of folders) {
        const key = folder.uri.toString();
        if (promptedFolders.has(key)) continue;

        const configUri = vscode.Uri.joinPath(folder.uri, 'koh.yaml');
        try {
            await vscode.workspace.fs.stat(configUri);
            // File exists — don't prompt
            promptedFolders.add(key);
            continue;
        } catch {
            // No koh.yaml — check if there are .asm files (heuristic mode)
        }

        const pattern = new vscode.RelativePattern(folder, '**/*.asm');
        const asmFiles = await vscode.workspace.findFiles(pattern, undefined, 1);
        if (asmFiles.length === 0) {
            // No .asm files — not relevant
            promptedFolders.add(key);
            continue;
        }

        promptedFolders.add(key);

        // Non-blocking prompt
        const choice = await vscode.window.showInformationMessage(
            'No koh.yaml found. Generate a project config for better LSP support?',
            'Generate',
            'Not now',
        );
        if (choice === 'Generate') {
            await generateConfigForFolder(folder);
        }
    }
}

// ---------------------------------------------------------------------------
// Activation / Deactivation
// ---------------------------------------------------------------------------

export async function activate(context: vscode.ExtensionContext) {
    log = vscode.window.createOutputChannel('Koh', { log: true });
    log.info('Koh extension activating...');

    // Register commands
    context.subscriptions.push(
        vscode.commands.registerCommand('koh.generateConfig', generateConfigCommand),
    );

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

    // After client starts, prompt about missing koh.yaml
    maybePromptGenerateConfig();
}

export function deactivate(): Thenable<void> | undefined {
    log?.info('Deactivating...');
    return undefined;
}
