import * as os from 'os';
import * as path from 'path';

/**
 * Canonical per-user toolchain root — mirrors rustup / `dotnet tool
 * install -g`. No admin required, survives extension reinstalls, and
 * reachable from a shell so CLI users can put `<root>/current/bin` on
 * their PATH.
 *
 *   Windows: %LOCALAPPDATA%\Koh\toolchain
 *   Linux:   $XDG_DATA_HOME/koh/toolchain  (default ~/.local/share/koh/toolchain)
 *   macOS:   ~/Library/Application Support/Koh/toolchain
 *
 * The Windows Inno Setup installer and the Unix install.sh write to
 * the same layout, so switching between "install via VS Code" and
 * "install via downloaded installer" doesn't break discovery.
 */
export function toolchainRoot(): string {
    if (process.platform === 'win32') {
        const lad = process.env.LOCALAPPDATA
            ?? path.join(os.homedir(), 'AppData', 'Local');
        return path.join(lad, 'Koh', 'toolchain');
    }
    if (process.platform === 'darwin') {
        return path.join(os.homedir(), 'Library', 'Application Support', 'Koh', 'toolchain');
    }
    const xdg = process.env.XDG_DATA_HOME
        ?? path.join(os.homedir(), '.local', 'share');
    return path.join(xdg, 'koh', 'toolchain');
}

/** Text file at `<root>/current` holding the active version string. */
export function currentPointerFile(): string {
    return path.join(toolchainRoot(), 'current');
}

/** Root directory of a specific installed toolchain version. */
export function versionRoot(version: string): string {
    return path.join(toolchainRoot(), version);
}

/** bin/ inside a version root — where the executables live. */
export function versionBin(version: string): string {
    return path.join(versionRoot(version), 'bin');
}

/** Metadata file written alongside each install: JSON `{ version, rid, installedAt }`. */
export function versionMetaFile(version: string): string {
    return path.join(versionRoot(version), 'version.json');
}

/** RIDs we currently build and publish in release assets. */
export const SUPPORTED_RIDS = ['win-x64', 'linux-x64', 'osx-arm64'] as const;
export type KohRid = typeof SUPPORTED_RIDS[number];

/**
 * Map the running host's platform+arch to a Koh RID, or `null` if
 * we don't publish artifacts for this combo. Callers should surface
 * a clear message ("unsupported platform: …") instead of silently
 * resolving to something wrong.
 */
export function detectRid(): KohRid | null {
    if (process.platform === 'win32' && process.arch === 'x64') return 'win-x64';
    if (process.platform === 'linux' && process.arch === 'x64') return 'linux-x64';
    if (process.platform === 'darwin' && process.arch === 'arm64') return 'osx-arm64';
    return null;
}

/**
 * Filename of the release archive for a given version+RID. Windows
 * uses zip because it's the native download format there; Unix uses
 * tar.gz so file modes (executable bit on the binaries) are preserved.
 */
export function archiveName(version: string, rid: KohRid): string {
    const ext = rid === 'win-x64' ? 'zip' : 'tar.gz';
    return `koh-toolchain-${version}-${rid}.${ext}`;
}

/** Executable name for the given tool, with Windows `.exe` suffix when applicable. */
export function executableName(toolStem: string): string {
    return process.platform === 'win32' ? `${toolStem}.exe` : toolStem;
}
