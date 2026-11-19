import { defineConfig } from '@vscode/test-cli';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const workspace = process.env.KOH_TEST_LSP_DIR ? mkdtempSync(join(tmpdir(), 'koh editor workspace ')) : undefined;
const userData = mkdtempSync(join(tmpdir(), 'koh editor profile '));
if (workspace) {
    writeFileSync(join(workspace, 'koh.yaml'), "version: 1\nprojects: [{name: game, entrypoint: 'main # 😀.asm'}]\n");
}
process.once('exit', () => {
    if (workspace) rmSync(workspace, { recursive: true, force: true });
    rmSync(userData, { recursive: true, force: true });
});

export default defineConfig({
    files: 'out/test/**/*.test.js',
    launchArgs: [...(workspace ? [workspace] : []), `--user-data-dir=${userData}`],
    mocha: {
        ui: 'tdd',
        timeout: 20_000,
        color: true,
    },
});
