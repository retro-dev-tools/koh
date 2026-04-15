import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { Logger } from '../core/Logger';
import { KohYaml, ResolvedTarget } from './WorkspaceConfig';

export class KohYamlReader {
    constructor(private readonly log: Logger) {}

    async read(folder: vscode.WorkspaceFolder): Promise<KohYaml | null> {
        const configPath = path.join(folder.uri.fsPath, 'koh.yaml');
        if (!fs.existsSync(configPath)) return null;

        const contents = fs.readFileSync(configPath, 'utf8');
        try {
            return this.parseMinimalYaml(contents);
        } catch (e) {
            this.log.error(`Failed to parse koh.yaml: ${e}`);
            return null;
        }
    }

    resolveTargets(yaml: KohYaml, folder: vscode.WorkspaceFolder): ResolvedTarget[] {
        return yaml.projects.map(p => ({
            name: p.name,
            entrypoint: path.join(folder.uri.fsPath, p.entrypoint),
            romPath: path.join(folder.uri.fsPath, 'build', `${p.name}.gb`),
            kdbgPath: path.join(folder.uri.fsPath, 'build', `${p.name}.kdbg`),
            workspaceFolder: folder.uri.fsPath,
        }));
    }

    /**
     * Minimal YAML parser sufficient for the koh.yaml schema. Avoids pulling in
     * a full YAML dependency for this simple format. If the schema grows, swap
     * in a real parser.
     */
    private parseMinimalYaml(text: string): KohYaml {
        const lines = text.split(/\r?\n/).map(l => l.replace(/\s+$/, ''));
        const projects: { name: string; entrypoint: string }[] = [];
        let version = 0;
        let inProjects = false;
        let current: Partial<{ name: string; entrypoint: string }> | null = null;

        for (const line of lines) {
            if (line.trim().startsWith('#') || line.trim() === '') continue;

            const vMatch = line.match(/^version:\s*(\d+)/);
            if (vMatch) { version = parseInt(vMatch[1], 10); continue; }

            if (line === 'projects:') { inProjects = true; continue; }

            if (inProjects && line.startsWith('  - ')) {
                if (current) projects.push(current as { name: string; entrypoint: string });
                current = {};
                const itemMatch = line.match(/^  - (\w+):\s*(.+)$/);
                if (itemMatch && current) (current as Record<string, string>)[itemMatch[1]] = itemMatch[2].trim();
                continue;
            }

            if (inProjects && line.startsWith('    ')) {
                const m = line.trim().match(/^(\w+):\s*(.+)$/);
                if (m && current) (current as Record<string, string>)[m[1]] = m[2].trim();
            }
        }
        if (current) projects.push(current as { name: string; entrypoint: string });

        return { version, projects };
    }
}
