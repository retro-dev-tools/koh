#!/usr/bin/env node
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const here = __dirname;
const extensionRoot = path.resolve(here, '..');
const repoRoot = path.resolve(extensionRoot, '..', '..');
const buildHashFile = path.join(extensionRoot, 'dist', 'emulator-app', '.build-hash');

if (!fs.existsSync(buildHashFile)) {
    console.error('emulator-app assets missing .build-hash; run npm run build:emulator-app:aot first');
    process.exit(1);
}

const recorded = fs.readFileSync(buildHashFile, 'utf8').trim();

const hashScript = process.platform === 'win32'
    ? path.join(repoRoot, 'scripts', 'compute-build-hash.ps1')
    : path.join(repoRoot, 'scripts', 'compute-build-hash.sh');
const cmd = process.platform === 'win32'
    ? `powershell -ExecutionPolicy Bypass -File "${hashScript}"`
    : `bash "${hashScript}"`;
const current = execSync(cmd, { cwd: repoRoot }).toString().trim();

if (recorded !== current) {
    console.error(`Stale emulator-app assets:\n  recorded: ${recorded}\n  current:  ${current}\nRun: npm run build:emulator-app:aot`);
    process.exit(1);
}

console.log(`emulator-app assets fresh (${current})`);
