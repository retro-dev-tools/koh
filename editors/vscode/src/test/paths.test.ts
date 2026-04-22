import * as assert from 'assert';
import * as path from 'path';
import {
    SUPPORTED_RIDS,
    archiveName,
    detectRid,
    executableName,
    toolchainRoot,
} from '../toolchain/paths';

suite('toolchain/paths', () => {
    const originalPlatform = process.platform;
    const originalArch = process.arch;
    const originalEnv = { ...process.env };

    teardown(() => {
        Object.defineProperty(process, 'platform', { value: originalPlatform });
        Object.defineProperty(process, 'arch', { value: originalArch });
        process.env = { ...originalEnv };
    });

    test('toolchainRoot on Windows honours LOCALAPPDATA', () => {
        Object.defineProperty(process, 'platform', { value: 'win32' });
        process.env.LOCALAPPDATA = 'D:\\AppData\\Local';
        assert.strictEqual(toolchainRoot(), path.join('D:\\AppData\\Local', 'Koh', 'toolchain'));
    });

    test('toolchainRoot on Linux honours XDG_DATA_HOME', () => {
        Object.defineProperty(process, 'platform', { value: 'linux' });
        process.env.XDG_DATA_HOME = '/custom/share';
        delete process.env.LOCALAPPDATA;
        assert.strictEqual(toolchainRoot(), path.join('/custom/share', 'koh', 'toolchain'));
    });

    test('toolchainRoot on Linux falls back to ~/.local/share', () => {
        Object.defineProperty(process, 'platform', { value: 'linux' });
        delete process.env.XDG_DATA_HOME;
        delete process.env.LOCALAPPDATA;
        // We don't assert an exact string here — just that ~/.local/share shows up.
        const root = toolchainRoot();
        assert.ok(root.endsWith(path.join('.local', 'share', 'koh', 'toolchain')), `unexpected root: ${root}`);
    });

    test('toolchainRoot on macOS lives under ~/Library', () => {
        Object.defineProperty(process, 'platform', { value: 'darwin' });
        delete process.env.XDG_DATA_HOME;
        delete process.env.LOCALAPPDATA;
        const root = toolchainRoot();
        assert.ok(
            root.includes(path.join('Library', 'Application Support', 'Koh', 'toolchain')),
            `unexpected root: ${root}`,
        );
    });

    test('detectRid maps platform+arch to published RIDs', () => {
        Object.defineProperty(process, 'platform', { value: 'win32' });
        Object.defineProperty(process, 'arch', { value: 'x64' });
        assert.strictEqual(detectRid(), 'win-x64');

        Object.defineProperty(process, 'platform', { value: 'linux' });
        Object.defineProperty(process, 'arch', { value: 'x64' });
        assert.strictEqual(detectRid(), 'linux-x64');

        Object.defineProperty(process, 'platform', { value: 'darwin' });
        Object.defineProperty(process, 'arch', { value: 'arm64' });
        assert.strictEqual(detectRid(), 'osx-arm64');
    });

    test('detectRid returns null for unsupported platforms', () => {
        Object.defineProperty(process, 'platform', { value: 'freebsd' });
        Object.defineProperty(process, 'arch', { value: 'x64' });
        assert.strictEqual(detectRid(), null);

        Object.defineProperty(process, 'platform', { value: 'darwin' });
        Object.defineProperty(process, 'arch', { value: 'x64' });
        assert.strictEqual(detectRid(), null, 'darwin-x64 has no published build');
    });

    test('archiveName matches the file names release workflows emit', () => {
        assert.strictEqual(archiveName('0.1.3', 'win-x64'), 'koh-toolchain-0.1.3-win-x64.zip');
        assert.strictEqual(archiveName('0.1.3', 'linux-x64'), 'koh-toolchain-0.1.3-linux-x64.tar.gz');
        assert.strictEqual(archiveName('0.1.3', 'osx-arm64'), 'koh-toolchain-0.1.3-osx-arm64.tar.gz');
    });

    test('executableName adds .exe on Windows only', () => {
        Object.defineProperty(process, 'platform', { value: 'win32' });
        assert.strictEqual(executableName('koh-lsp'), 'koh-lsp.exe');

        Object.defineProperty(process, 'platform', { value: 'linux' });
        assert.strictEqual(executableName('koh-lsp'), 'koh-lsp');
    });

    test('SUPPORTED_RIDS matches the release workflow matrix', () => {
        // Cheap tripwire: if CI's matrix drifts, this fails and forces
        // the runtime detectRid + archive logic to catch up.
        assert.deepStrictEqual([...SUPPORTED_RIDS], ['win-x64', 'linux-x64', 'osx-arm64']);
    });
});
