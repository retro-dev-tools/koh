import * as vscode from 'vscode';
import * as path from 'path';
import { ResolvedTarget } from '../config/WorkspaceConfig';

export interface KohBinaries {
    readonly asm: string;
    readonly link: string;
}

export function createBuildTask(binaries: KohBinaries, target: ResolvedTarget): vscode.Task {
    const objectPath = path.join(path.dirname(target.romPath), `${target.name}.o`);
    const buildDir = path.dirname(target.romPath);
    const symPath = path.join(buildDir, `${target.name}.sym`);

    const mkdirCmd = process.platform === 'win32'
        ? `if not exist "${buildDir}" mkdir "${buildDir}"`
        : `mkdir -p "${buildDir}"`;

    const execution = new vscode.ShellExecution(
        `${mkdirCmd} && ` +
        `"${binaries.asm}" "${target.entrypoint}" -o "${objectPath}" && ` +
        `"${binaries.link}" "${objectPath}" -o "${target.romPath}" -n "${symPath}" -d "${target.debugInfoPath}"`,
        { cwd: target.workspaceFolder }
    );

    const task = new vscode.Task(
        { type: 'koh', target: target.name },
        vscode.TaskScope.Workspace,
        `build ${target.name}`,
        'koh',
        execution
    );
    task.group = vscode.TaskGroup.Build;
    return task;
}
