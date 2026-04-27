import * as crypto from 'crypto';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { createGunzip } from 'zlib';
import { extract as createTarExtractor } from 'tar';
import yauzl from 'yauzl';
import { Logger } from '../core/Logger';
import { GitHubReleaseClient } from './GitHubReleaseClient';
import {
    archiveName,
    detectRid,
    KohRid,
    toolchainRoot,
    versionBin,
    versionMetaFile,
    versionRoot,
} from './paths';
import { ReleaseInfo, VersionMeta } from './types';
import { ToolchainResolver } from './ToolchainResolver';

/**
 * Downloads and unpacks a toolchain release into the canonical
 * per-user path. Every install goes through a temp staging directory
 * and is atomically renamed into place at the end — a crash midway
 * can't leave a half-extracted bin/ around.
 */
export class ToolchainInstaller {
    constructor(
        private readonly log: Logger,
        private readonly client: GitHubReleaseClient,
        private readonly resolver: ToolchainResolver,
    ) {}

    /**
     * Install `release` for the current host's RID. If the user is on
     * an unsupported platform, throws early with a clear message —
     * better than silently downloading and failing at extraction.
     */
    async install(release: ReleaseInfo, progress?: vscode.Progress<{ message?: string; increment?: number }>): Promise<void> {
        const rid = detectRid();
        if (!rid) {
            throw new Error(`no Koh toolchain build for platform ${process.platform}/${process.arch}`);
        }

        const version = release.version;
        const archiveUrl = this.client.archiveUrlFor(release, rid);
        const name = archiveName(version, rid);

        this.log.info(`installing toolchain ${version} (${rid}) from ${archiveUrl}`);
        progress?.report({ message: `Fetching ${name}…` });

        // Download + checksums file in parallel.
        const [checksums, archiveBuf] = await Promise.all([
            this.client.downloadText(release.checksumsUrl).catch(err => {
                throw new Error(`failed to fetch SHA256SUMS: ${err}`);
            }),
            this.streamDownload(archiveUrl, progress),
        ]);

        const expected = findChecksum(checksums, name);
        if (!expected) {
            throw new Error(`${name} not listed in SHA256SUMS.txt`);
        }
        const actual = crypto.createHash('sha256').update(archiveBuf).digest('hex');
        if (actual !== expected) {
            throw new Error(`checksum mismatch for ${name}: expected ${expected}, got ${actual}`);
        }
        this.log.info(`checksum ok (${actual})`);

        progress?.report({ message: `Extracting ${name}…`, increment: 5 });

        // Stage into a sibling ".<version>.tmp" directory so that even
        // if extraction fails midway the previous install (if any)
        // stays usable.
        const stageDir = path.join(toolchainRoot(), `.${version}.tmp-${process.pid}`);
        await removeDir(stageDir);
        fs.mkdirSync(stageDir, { recursive: true });
        const binStage = path.join(stageDir, 'bin');
        fs.mkdirSync(binStage);

        if (rid === 'win-x64') {
            await extractZipBuffer(archiveBuf, binStage);
        } else {
            await extractTarGzBuffer(archiveBuf, binStage);
        }

        // Write the metadata file so later resolves can report the
        // version without going back to the pointer file.
        const meta: VersionMeta = {
            version,
            rid,
            installedAt: new Date().toISOString(),
        };
        fs.writeFileSync(path.join(stageDir, 'version.json'), JSON.stringify(meta, null, 2), 'utf8');

        // Replace any existing install of the same version (upgrade /
        // retry path), then atomically rename the stage into place.
        const finalDir = versionRoot(version);
        await removeDir(finalDir);
        fs.renameSync(stageDir, finalDir);
        this.log.info(`extracted to ${finalDir}`);

        this.resolver.writeCurrentPointer(version);
        this.log.info(`current → ${version}`);
    }

    /** Delete a managed version. Refuses to delete the one pointed at by `current`. */
    async uninstall(version: string): Promise<void> {
        if (this.resolver.readCurrentPointer() === version) {
            throw new Error(`cannot uninstall ${version}: it's the current toolchain. Switch first.`);
        }
        await removeDir(versionRoot(version));
        this.log.info(`uninstalled ${version}`);
    }

    private async streamDownload(url: string, progress?: vscode.Progress<{ message?: string; increment?: number }>): Promise<Buffer> {
        const chunks: Buffer[] = [];
        let lastPct = 0;
        await this.client.download(
            url,
            buf => chunks.push(buf),
            (sent, total) => {
                if (!progress || !total) return;
                const pct = Math.floor((sent / total) * 100);
                // vscode.Progress expects *increment* (delta), not absolute;
                // clamp to whole-percent hops so we don't spam the UI.
                if (pct > lastPct) {
                    progress.report({ message: `Downloading: ${pct}%`, increment: pct - lastPct });
                    lastPct = pct;
                }
            },
        );
        return Buffer.concat(chunks);
    }
}

function findChecksum(sha256sums: string, file: string): string | null {
    // Format: "<hex>  <filename>\n" (two spaces) per line.
    for (const raw of sha256sums.split('\n')) {
        const line = raw.trim();
        if (!line) continue;
        const m = /^([0-9a-f]{64})\s+(?:\*)?(\S+)$/i.exec(line);
        if (!m) continue;
        if (m[2] === file) return m[1].toLowerCase();
    }
    return null;
}

async function removeDir(dir: string): Promise<void> {
    try {
        await fs.promises.rm(dir, { recursive: true, force: true });
    } catch (err) {
        if ((err as NodeJS.ErrnoException).code !== 'ENOENT') throw err;
    }
}

async function extractTarGzBuffer(buf: Buffer, destDir: string): Promise<void> {
    await new Promise<void>((resolve, reject) => {
        const tmp = path.join(os.tmpdir(), `koh-tc-${process.pid}-${Date.now()}.tar.gz`);
        fs.writeFileSync(tmp, buf);
        const src = fs.createReadStream(tmp);
        const gunzip = createGunzip();
        const extractor = createTarExtractor({ cwd: destDir });
        src.pipe(gunzip).pipe(extractor);
        extractor.on('finish', () => {
            fs.promises.unlink(tmp).catch(() => { /* leave tmp file behind on cleanup error — not worth failing the install */ });
            resolve();
        });
        src.on('error', reject);
        gunzip.on('error', reject);
        extractor.on('error', reject);
    });
}

function extractZipBuffer(buf: Buffer, destDir: string): Promise<void> {
    return new Promise<void>((resolve, reject) => {
        yauzl.fromBuffer(buf, { lazyEntries: true }, (err, zip) => {
            if (err || !zip) { reject(err ?? new Error('yauzl returned no handle')); return; }
            zip.on('error', reject);
            zip.on('end', resolve);
            zip.readEntry();
            zip.on('entry', entry => {
                let target: string;
                try {
                    target = resolveArchiveEntryTarget(destDir, entry.fileName);
                } catch (err) {
                    reject(err);
                    zip.close();
                    return;
                }
                // Zip entries ending with '/' are directories.
                if (/\/$/.test(entry.fileName)) {
                    fs.mkdirSync(target, { recursive: true });
                    zip.readEntry();
                    return;
                }
                fs.mkdirSync(path.dirname(target), { recursive: true });
                zip.openReadStream(entry, (err2, stream) => {
                    if (err2 || !stream) { reject(err2 ?? new Error('openReadStream returned no stream')); return; }
                    const out = fs.createWriteStream(target);
                    stream.pipe(out);
                    out.on('close', () => zip.readEntry());
                    out.on('error', reject);
                    stream.on('error', reject);
                });
            });
        });
    });
}

export function resolveArchiveEntryTarget(destDir: string, entryName: string): string {
    const normalized = entryName.replace(/\\/g, '/');
    if (path.isAbsolute(normalized)) {
        throw new Error(`archive entry uses absolute path: ${entryName}`);
    }

    const parts = normalized.split('/').filter(part => part.length > 0);
    if (parts.some(part => part === '..')) {
        throw new Error(`archive entry escapes destination: ${entryName}`);
    }

    const root = path.resolve(destDir);
    const target = path.resolve(root, ...parts);
    const relative = path.relative(root, target);
    if (relative.startsWith('..') || path.isAbsolute(relative)) {
        throw new Error(`archive entry escapes destination: ${entryName}`);
    }
    return target;
}
