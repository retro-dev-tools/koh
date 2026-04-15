#!/usr/bin/env node
// Copies the published Blazor WASM assets into editors/vscode/dist/emulator-app/
// and writes .build-hash matching scripts/compute-build-hash.{ps1,sh}.

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const here = __dirname;
const extensionRoot = path.resolve(here, '..');
const repoRoot = path.resolve(extensionRoot, '..', '..');
const publishRoot = path.join(extensionRoot, '.publish-emu-app');
const publishWwwroot = path.join(publishRoot, 'wwwroot');
const distTarget = path.join(extensionRoot, 'dist', 'emulator-app');

if (!fs.existsSync(publishWwwroot)) {
    console.error(`publish wwwroot not found at ${publishWwwroot}`);
    process.exit(1);
}

fs.rmSync(distTarget, { recursive: true, force: true });
fs.mkdirSync(distTarget, { recursive: true });

copyRecursive(publishWwwroot, distTarget);

// Compute and write content hash.
const hashScript = process.platform === 'win32'
    ? path.join(repoRoot, 'scripts', 'compute-build-hash.ps1')
    : path.join(repoRoot, 'scripts', 'compute-build-hash.sh');
const cmd = process.platform === 'win32'
    ? `powershell -ExecutionPolicy Bypass -File "${hashScript}"`
    : `bash "${hashScript}"`;
const hash = execSync(cmd, { cwd: repoRoot }).toString().trim();
fs.writeFileSync(path.join(distTarget, '.build-hash'), hash + '\n', 'utf8');

console.log(`Emulator app copied to ${distTarget} (hash: ${hash})`);

function copyRecursive(src, dst) {
    const entries = fs.readdirSync(src, { withFileTypes: true });
    for (const entry of entries) {
        const s = path.join(src, entry.name);
        const d = path.join(dst, entry.name);
        if (entry.isDirectory()) {
            fs.mkdirSync(d, { recursive: true });
            copyRecursive(s, d);
        } else {
            fs.copyFileSync(s, d);
        }
    }
}
