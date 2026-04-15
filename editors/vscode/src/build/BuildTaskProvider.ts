import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { KohYamlReader } from '../config/KohYamlReader';
import { resolveKohBinaries } from './binaryResolver';
import { createBuildTask } from './KohBuildTask';

export class BuildTaskProvider implements vscode.TaskProvider {
    constructor(
        private readonly log: Logger,
        private readonly yamlReader: KohYamlReader
    ) {}

    async provideTasks(): Promise<vscode.Task[]> {
        const binaries = resolveKohBinaries(this.log);
        if (!binaries) return [];

        const tasks: vscode.Task[] = [];
        for (const folder of vscode.workspace.workspaceFolders ?? []) {
            const yaml = await this.yamlReader.read(folder);
            if (!yaml) continue;
            const targets = this.yamlReader.resolveTargets(yaml, folder);
            for (const target of targets) {
                tasks.push(createBuildTask(binaries, target));
            }
        }
        return tasks;
    }

    resolveTask(_task: vscode.Task): vscode.Task | undefined {
        return undefined;
    }

    register(): vscode.Disposable {
        return vscode.tasks.registerTaskProvider('koh', this);
    }
}
