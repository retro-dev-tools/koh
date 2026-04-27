import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { Logger } from '../core/Logger';
import {
    currentPointerFile,
    executableName,
    toolchainRoot,
    versionBin,
    versionMetaFile,
    versionRoot,
} from './paths';
import { ToolchainLocation, VersionMeta } from './types';

/**
 * Finds the toolchain the extension should use at startup and on
 * demand. Precedence (first hit wins):
 *
 *   1. `koh.toolchainPath` setting — explicit override for devs
 *      building from source (points at src/Koh.X/bin/Release/... or a
 *      local publish directory).
 *   2. Managed install — whichever version `<toolchainRoot>/current`
 *      points at, falling back to the newest subdirectory if the
 *      pointer is stale or missing.
 *   3. PATH — if `koh-lsp[.exe]` is on PATH, the directory containing
 *      it is treated as an externally-managed toolchain.
 *
 * `findAnchorExe` ensures we only report a directory as a toolchain
 * if the LSP server is actually present — stops us from returning an
 * empty directory just because the user's override points at it.
 */
export class ToolchainResolver {
    constructor(private readonly log: Logger) {}

    resolve(): ToolchainLocation | null {
        return this.fromSetting()
            ?? this.fromManagedInstall()
            ?? this.fromPath();
    }

    /** All versions that live under `<toolchainRoot>/` and contain a usable bin/. Newest-first. */
    listManaged(): { version: string; meta: VersionMeta | null }[] {
        const root = toolchainRoot();
        if (!fs.existsSync(root)) return [];

        const entries: { version: string; meta: VersionMeta | null }[] = [];
        for (const name of fs.readdirSync(root)) {
            const full = path.join(root, name);
            if (!fs.statSync(full).isDirectory()) continue;
            if (!fs.existsSync(path.join(full, 'bin', executableName('koh-lsp')))) continue;
            entries.push({ version: name, meta: this.readMeta(name) });
        }
        entries.sort((a, b) => compareVersions(b.version, a.version));
        return entries;
    }

    /** Current version pointer ("which managed install is active"), or null if nothing installed. */
    readCurrentPointer(): string | null {
        const file = currentPointerFile();
        if (!fs.existsSync(file)) return null;
        try {
            const v = fs.readFileSync(file, 'utf8').trim();
            return v.length > 0 ? v : null;
        } catch (err) {
            this.log.warn(`failed to read ${file}: ${err}`);
            return null;
        }
    }

    /** Update `<root>/current` to point at `version`. Writes atomically. */
    writeCurrentPointer(version: string): void {
        const file = currentPointerFile();
        fs.mkdirSync(path.dirname(file), { recursive: true });
        const tmp = `${file}.tmp`;
        fs.writeFileSync(tmp, version, 'utf8');
        fs.renameSync(tmp, file);
    }

    readMeta(version: string): VersionMeta | null {
        const file = versionMetaFile(version);
        if (!fs.existsSync(file)) return null;
        try {
            const parsed = JSON.parse(fs.readFileSync(file, 'utf8'));
            if (
                typeof parsed?.version === 'string'
                && typeof parsed?.rid === 'string'
                && typeof parsed?.installedAt === 'string'
            ) {
                return parsed;
            }
        } catch (err) {
            this.log.warn(`failed to parse ${file}: ${err}`);
        }
        return null;
    }

    private fromSetting(): ToolchainLocation | null {
        const setting = vscode.workspace.getConfiguration('koh').get<string>('toolchainPath');
        if (!setting || setting.length === 0) return null;

        // The setting may point directly at a bin/ or at a version root; accept both.
        const candidates = [setting, path.join(setting, 'bin')];
        for (const dir of candidates) {
            if (hasAnchor(dir)) {
                this.log.info(`toolchain from setting: ${dir}`);
                const version = this.inferVersionFromBin(dir) ?? '(custom)';
                return { version, binDir: dir, source: 'settingOverride' };
            }
        }
        this.log.warn(`koh.toolchainPath = "${setting}" but no koh-lsp found there`);
        return null;
    }

    private fromManagedInstall(): ToolchainLocation | null {
        const pointer = this.readCurrentPointer();
        if (pointer) {
            const dir = versionBin(pointer);
            if (hasAnchor(dir)) {
                this.log.info(`toolchain from managed install: ${pointer} (${dir})`);
                return { version: pointer, binDir: dir, source: 'managedInstall' };
            }
            this.log.warn(`current pointer = "${pointer}" but bin dir missing or empty`);
        }

        const entries = this.listManaged();
        if (entries.length === 0) return null;
        const newest = entries[0].version;
        this.log.info(`toolchain from managed install (newest fallback): ${newest}`);
        return { version: newest, binDir: versionBin(newest), source: 'managedInstall' };
    }

    private fromPath(): ToolchainLocation | null {
        const pathEnv = process.env.PATH ?? '';
        const delim = process.platform === 'win32' ? ';' : ':';
        const exe = executableName('koh-lsp');
        for (const dir of pathEnv.split(delim)) {
            if (!dir) continue;
            if (fs.existsSync(path.join(dir, exe))) {
                this.log.info(`toolchain from PATH: ${dir}`);
                const version = this.inferVersionFromBin(dir) ?? '(path)';
                return { version, binDir: dir, source: 'path' };
            }
        }
        return null;
    }

    /**
     * bin/ sits inside a versioned directory that may have a version.json
     * sibling one level up. Read it if we can; return null otherwise so
     * callers fall back to a placeholder ("(custom)", "(path)").
     */
    private inferVersionFromBin(binDir: string): string | null {
        const metaFile = path.join(binDir, '..', 'version.json');
        if (!fs.existsSync(metaFile)) return null;
        try {
            const parsed = JSON.parse(fs.readFileSync(metaFile, 'utf8'));
            return typeof parsed?.version === 'string' ? parsed.version : null;
        } catch {
            return null;
        }
    }
}

function hasAnchor(dir: string): boolean {
    return fs.existsSync(path.join(dir, executableName('koh-lsp')));
}

/**
 * Semver-aware compare. Works for plain dotted numeric versions
 * (0.1.3, 0.1.10). Pre-release suffixes sort before the release.
 */
export function compareVersions(a: string, b: string): number {
    const [aMain, aPre] = splitSemver(a);
    const [bMain, bPre] = splitSemver(b);
    const aParts = aMain.split('.').map(n => parseInt(n, 10) || 0);
    const bParts = bMain.split('.').map(n => parseInt(n, 10) || 0);
    const len = Math.max(aParts.length, bParts.length);
    for (let i = 0; i < len; i++) {
        const d = (aParts[i] ?? 0) - (bParts[i] ?? 0);
        if (d !== 0) return d;
    }
    // Releases sort after their pre-releases: 0.1.3 > 0.1.3-rc.1.
    if (aPre && !bPre) return -1;
    if (!aPre && bPre) return 1;
    if (aPre && bPre) return aPre.localeCompare(bPre);
    return 0;
}

function splitSemver(v: string): [string, string | null] {
    const dash = v.indexOf('-');
    return dash >= 0 ? [v.slice(0, dash), v.slice(dash + 1)] : [v, null];
}
