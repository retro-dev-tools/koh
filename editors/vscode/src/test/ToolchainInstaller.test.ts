import * as assert from 'assert';
import * as os from 'os';
import * as path from 'path';
import { resolveArchiveEntryTarget } from '../toolchain/ToolchainInstaller';

suite('ToolchainInstaller archive paths', () => {
    test('resolves normal entries inside the destination', () => {
        const root = path.join(os.tmpdir(), 'koh-toolchain-test');
        const target = resolveArchiveEntryTarget(root, 'subdir/koh-lsp.exe');

        assert.strictEqual(target, path.resolve(root, 'subdir', 'koh-lsp.exe'));
    });

    test('rejects parent-directory traversal with forward slashes', () => {
        const root = path.join(os.tmpdir(), 'koh-toolchain-test');

        assert.throws(
            () => resolveArchiveEntryTarget(root, '../../outside.exe'),
            /escapes destination/,
        );
    });

    test('rejects parent-directory traversal with backslashes', () => {
        const root = path.join(os.tmpdir(), 'koh-toolchain-test');

        assert.throws(
            () => resolveArchiveEntryTarget(root, '..\\..\\outside.exe'),
            /escapes destination/,
        );
    });

    test('rejects absolute archive entry paths', () => {
        const root = path.join(os.tmpdir(), 'koh-toolchain-test');
        const absoluteEntry = path.join(path.parse(root).root, 'outside', 'koh-lsp.exe');

        assert.throws(
            () => resolveArchiveEntryTarget(root, absoluteEntry),
            /absolute path/,
        );
    });
});
