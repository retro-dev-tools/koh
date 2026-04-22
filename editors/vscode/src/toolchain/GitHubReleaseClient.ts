import * as https from 'https';
import { Logger } from '../core/Logger';
import { ReleaseInfo } from './types';
import { archiveName, KohRid } from './paths';

/**
 * Tiny read-only client for the GitHub releases API. Anonymous calls
 * are rate-limited to 60/hour per IP — we only use it on activation
 * (one `listReleases` call to check for updates) and when the user
 * explicitly installs, so we won't come close to that limit.
 */
export class GitHubReleaseClient {
    private readonly apiHost = 'api.github.com';
    private readonly owner: string;
    private readonly repo: string;

    constructor(private readonly log: Logger, repoSlug: string) {
        const [owner, repo] = repoSlug.split('/');
        if (!owner || !repo) {
            throw new Error(`invalid repo slug "${repoSlug}", expected "owner/repo"`);
        }
        this.owner = owner;
        this.repo = repo;
    }

    /** All tool releases newest-first — i.e., releases whose tag starts with `tools-v`. */
    async listToolReleases(): Promise<ReleaseInfo[]> {
        const raw = await this.getJson<GitHubRelease[]>(`/repos/${this.owner}/${this.repo}/releases?per_page=30`);
        const releases: ReleaseInfo[] = [];
        for (const r of raw) {
            const info = this.toReleaseInfo(r);
            if (info) releases.push(info);
        }
        return releases;
    }

    /** Latest tool release, or null if none exist. Prefers non-drafts and includes pre-releases. */
    async latestToolRelease(): Promise<ReleaseInfo | null> {
        const all = await this.listToolReleases();
        return all.length > 0 ? all[0] : null;
    }

    /**
     * Stream a URL into the given consumer. Returns total bytes; calls
     * `onProgress(bytesSoFar, contentLength ?? null)` as data arrives.
     * Follows up to 5 redirects (GitHub release downloads redirect to
     * blob storage).
     */
    async download(
        url: string,
        onChunk: (chunk: Buffer) => void,
        onProgress: (sent: number, total: number | null) => void,
    ): Promise<number> {
        let current = url;
        for (let i = 0; i < 5; i++) {
            const { status, headers, body } = await this.openStream(current);
            const loc = headers.location;
            const nextHop = Array.isArray(loc) ? loc[0] : loc;
            if (status >= 300 && status < 400 && nextHop) {
                current = new URL(nextHop, current).toString();
                body.resume();
                continue;
            }
            if (status !== 200) {
                body.resume();
                throw new Error(`download failed: ${status} for ${current}`);
            }
            const total = headers['content-length']
                ? parseInt(headers['content-length'] as string, 10)
                : null;
            let sent = 0;
            for await (const chunk of body) {
                const buf = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
                sent += buf.length;
                onChunk(buf);
                onProgress(sent, total);
            }
            return sent;
        }
        throw new Error(`too many redirects fetching ${url}`);
    }

    /** Plain-text download, used for the SHA256SUMS.txt file. */
    async downloadText(url: string): Promise<string> {
        const chunks: Buffer[] = [];
        await this.download(url, buf => chunks.push(buf), () => { /* no progress needed for tiny files */ });
        return Buffer.concat(chunks).toString('utf8');
    }

    private toReleaseInfo(r: GitHubRelease): ReleaseInfo | null {
        if (r.draft) return null;
        const match = /^tools-v(\d+\.\d+\.\d+(?:-[\w.-]+)?)$/.exec(r.tag_name);
        if (!match) return null;
        const version = match[1];
        // Callers pick the RID they want — we keep the archive URL for
        // *all* RIDs in the release so we can enumerate compatibility
        // later, but for now just look for any archive to confirm the
        // release actually shipped.
        const anyArchive = r.assets.find(a => a.name.startsWith(`koh-toolchain-${version}-`));
        if (!anyArchive) return null;

        // Callers pass the RID separately; we synthesise per-rid URLs
        // from the release's browser_download_url prefix.
        const prefix = anyArchive.browser_download_url.slice(
            0, anyArchive.browser_download_url.length - anyArchive.name.length);
        return {
            version,
            tag: r.tag_name,
            archiveUrl: prefix,   // Incomplete — callers append archiveName(version, rid).
            archiveName: '',      // Filled in per-rid by the installer.
            checksumsUrl: `${prefix}SHA256SUMS.txt`,
        };
    }

    /** Build the real per-RID archive URL from a ReleaseInfo returned by listToolReleases. */
    archiveUrlFor(release: ReleaseInfo, rid: KohRid): string {
        return release.archiveUrl + archiveName(release.version, rid);
    }

    private async getJson<T>(pathAndQuery: string): Promise<T> {
        const body = await this.request(pathAndQuery);
        try {
            return JSON.parse(body) as T;
        } catch (err) {
            throw new Error(`failed to parse JSON from ${pathAndQuery}: ${err}`);
        }
    }

    private request(pathAndQuery: string): Promise<string> {
        return new Promise((resolve, reject) => {
            const req = https.get({
                host: this.apiHost,
                path: pathAndQuery,
                headers: {
                    'User-Agent': `koh-asm-extension`,
                    'Accept': 'application/vnd.github+json',
                    'X-GitHub-Api-Version': '2022-11-28',
                },
            }, res => {
                if ((res.statusCode ?? 0) !== 200) {
                    res.resume();
                    reject(new Error(`GET ${pathAndQuery} → ${res.statusCode}`));
                    return;
                }
                const chunks: Buffer[] = [];
                res.on('data', c => chunks.push(Buffer.isBuffer(c) ? c : Buffer.from(c)));
                res.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
                res.on('error', reject);
            });
            req.on('error', reject);
        });
    }

    private openStream(url: string): Promise<{ status: number; headers: NodeJS.Dict<string | string[]>; body: NodeJS.ReadableStream }> {
        return new Promise((resolve, reject) => {
            const req = https.get(url, {
                headers: {
                    'User-Agent': `koh-asm-extension`,
                    'Accept': 'application/octet-stream',
                },
            }, res => {
                resolve({ status: res.statusCode ?? 0, headers: res.headers, body: res });
            });
            req.on('error', reject);
        });
    }
}

interface GitHubRelease {
    readonly tag_name: string;
    readonly draft: boolean;
    readonly prerelease: boolean;
    readonly assets: { readonly name: string; readonly browser_download_url: string }[];
}
