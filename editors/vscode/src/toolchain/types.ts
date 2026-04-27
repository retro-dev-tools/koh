import { KohRid } from './paths';

/** A toolchain install the extension can actually use — version + directory its binaries live in. */
export interface ToolchainLocation {
    readonly version: string;
    readonly binDir: string;
    /** How we found it — useful in diagnostics and decides update-check behaviour. */
    readonly source: 'settingOverride' | 'managedInstall' | 'path';
}

/** Parsed form of `<root>/<ver>/version.json`. */
export interface VersionMeta {
    readonly version: string;
    readonly rid: KohRid;
    readonly installedAt: string;
}

/** Release we'd install or upgrade to. */
export interface ReleaseInfo {
    readonly version: string;
    readonly tag: string;
    readonly archiveUrl: string;
    readonly archiveName: string;
    readonly checksumsUrl: string;
}
