export interface KohYamlProject {
    name: string;
    entrypoint: string;
}

export interface KohYaml {
    version: number;
    projects: KohYamlProject[];
}

export interface ResolvedTarget {
    name: string;
    entrypoint: string;          // absolute path
    romPath: string;             // absolute output path for the .gb
    kdbgPath: string;            // absolute output path for the .kdbg
    workspaceFolder: string;
}
