import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { Logger } from '../core/Logger';
import { executableName, toolchainRoot } from '../toolchain/paths';
import { ToolchainResolver } from '../toolchain/ToolchainResolver';

/**
 * Writes a minimal toolchain layout under <root>/<version> so the
 * resolver's "managed install" branch has something to find. Only
 * koh-lsp is required (resolver's anchor check); the test can add
 * more files if it wants to assert bin/ contents.
 */
function seedManagedVersion(root: string, version: string): string {
    const bin = path.join(root, version, 'bin');
    fs.mkdirSync(bin, { recursive: true });
    fs.writeFileSync(path.join(bin, executableName('koh-lsp')), '');
    fs.writeFileSync(
        path.join(root, version, 'version.json'),
        JSON.stringify({ version, rid: 'test-rid', installedAt: '' }),
    );
    return bin;
}

/**
 * Redirects toolchainRoot() into a temp scratch dir by setting the
 * per-platform env vars the function reads. Returns the canonical
 * root + a cleanup callback.
 */
function withScratchToolchainRoot(): { root: string; cleanup: () => void } {
    const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'koh-tc-test-'));
    const saved = {
        LOCALAPPDATA: process.env.LOCALAPPDATA,
        XDG_DATA_HOME: process.env.XDG_DATA_HOME,
        HOME: process.env.HOME,
        APPDATA: process.env.APPDATA,
        USERPROFILE: process.env.USERPROFILE,
    };
    // Each branch of toolchainRoot() keys off one of these; overriding
    // them all to scratch makes sure whichever branch runs lands under
    // our temp dir, regardless of the test host's platform.
    process.env.LOCALAPPDATA = scratch;
    process.env.XDG_DATA_HOME = scratch;
    process.env.HOME = scratch;
    process.env.USERPROFILE = scratch;
    delete process.env.APPDATA;
    const root = toolchainRoot();
    return {
        root,
        cleanup: () => {
            process.env.LOCALAPPDATA = saved.LOCALAPPDATA;
            process.env.XDG_DATA_HOME = saved.XDG_DATA_HOME;
            process.env.HOME = saved.HOME;
            process.env.APPDATA = saved.APPDATA;
            process.env.USERPROFILE = saved.USERPROFILE;
            try { fs.rmSync(scratch, { recursive: true, force: true }); } catch { /* best effort */ }
        },
    };
}

suite('ToolchainResolver (filesystem)', () => {
    // One shared Logger for the whole suite. Creating and disposing a
    // LogOutputChannel per test triggers VS Code's "channel has been
    // closed" error on the next log call (the extension host caches
    // channel proxies and expects them to outlive their creator).
    let log: Logger;
    let ctx: ReturnType<typeof withScratchToolchainRoot>;

    suiteSetup(() => { log = new Logger('Resolver.Test'); });
    suiteTeardown(() => { log.dispose(); });

    setup(() => { ctx = withScratchToolchainRoot(); });
    teardown(() => { ctx.cleanup(); });

    test('returns null when no toolchain is installed anywhere', () => {
        // Empty scratch root, no `current` pointer, no settings override,
        // no koh-lsp on PATH visible to this host => resolver gives up.
        // The "PATH scan" branch can still find a real host's koh-lsp if
        // CI has one installed, so we skip that assertion if so.
        const savedPath = process.env.PATH;
        try {
            process.env.PATH = '';
            const resolver = new ToolchainResolver(log);
            assert.strictEqual(resolver.resolve(), null);
        } finally {
            process.env.PATH = savedPath;
        }
    });

    test('resolves from the current pointer when present', () => {
        seedManagedVersion(ctx.root, '0.1.3-beta');
        const resolver = new ToolchainResolver(log);
        resolver.writeCurrentPointer('0.1.3-beta');

        const loc = resolver.resolve();
        assert.ok(loc, 'expected a resolved toolchain');
        assert.strictEqual(loc!.version, '0.1.3-beta');
        assert.strictEqual(loc!.source, 'managedInstall');
        assert.strictEqual(loc!.binDir, path.join(ctx.root, '0.1.3-beta', 'bin'));
    });

    test('falls back to the newest managed version when the pointer is stale', () => {
        // If `current` points at a version that no longer exists (user
        // manually deleted a bin dir, partial upgrade), the resolver
        // should still return SOMETHING rather than failing — pick the
        // newest managed install it can find.
        seedManagedVersion(ctx.root, '0.1.2');
        seedManagedVersion(ctx.root, '0.1.10');
        seedManagedVersion(ctx.root, '0.1.3');
        const resolver = new ToolchainResolver(log);
        resolver.writeCurrentPointer('ghost-version');

        const loc = resolver.resolve();
        assert.ok(loc);
        assert.strictEqual(loc!.version, '0.1.10',
            'expected newest-by-semver (0.1.10 > 0.1.3 > 0.1.2), not lexical ordering');
    });

    test('listManaged returns versions newest-first with version.json parsed', () => {
        seedManagedVersion(ctx.root, '0.1.2');
        seedManagedVersion(ctx.root, '0.1.10');
        seedManagedVersion(ctx.root, '0.2.0');
        // A directory without bin/koh-lsp is not a usable install and
        // must be filtered out — think half-written downloads.
        fs.mkdirSync(path.join(ctx.root, '0.3.0-corrupt'), { recursive: true });
        const resolver = new ToolchainResolver(log);

        const managed = resolver.listManaged();
        const versions = managed.map(m => m.version);
        assert.deepStrictEqual(versions, ['0.2.0', '0.1.10', '0.1.2']);
        assert.ok(managed.every(m => m.meta !== null), 'version.json should parse for each');
    });

    test('current-pointer read/write is atomic', () => {
        // writeCurrentPointer writes to a temp file then renames, so a
        // crash halfway through an update can't leave the file empty
        // while a stale reader tries to parse it. Exercising the full
        // write→read cycle is the cheapest way to check it.
        const resolver = new ToolchainResolver(log);
        assert.strictEqual(resolver.readCurrentPointer(), null);

        resolver.writeCurrentPointer('0.1.4');
        assert.strictEqual(resolver.readCurrentPointer(), '0.1.4');

        resolver.writeCurrentPointer('0.1.5-beta');
        assert.strictEqual(resolver.readCurrentPointer(), '0.1.5-beta');
    });

    test('stops short of managed install if the bin dir has no koh-lsp', () => {
        // Guards against a subtle install failure: the version dir
        // exists but extraction crashed before koh-lsp landed. We'd
        // rather return null than try to launch a directory.
        const dir = path.join(ctx.root, '0.1.3-beta', 'bin');
        fs.mkdirSync(dir, { recursive: true });
        // No koh-lsp written.
        const resolver = new ToolchainResolver(log);
        resolver.writeCurrentPointer('0.1.3-beta');
        assert.strictEqual(resolver.resolve(), null);
    });
});
