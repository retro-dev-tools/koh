import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { KohYamlReader } from '../config/KohYamlReader';
import { KohLaunchConfiguration } from './launchTypes';
import { TargetSelector } from './TargetSelector';

export class KohConfigurationProvider implements vscode.DebugConfigurationProvider {
    constructor(
        private readonly log: Logger,
        private readonly yamlReader: KohYamlReader,
        private readonly targetSelector: TargetSelector
    ) {}

    async resolveDebugConfiguration(
        folder: vscode.WorkspaceFolder | undefined,
        config: vscode.DebugConfiguration
    ): Promise<vscode.DebugConfiguration | null | undefined> {
        // Case 1: F5 with no launch.json at all — config.type is empty.
        if (!config.type) {
            return await this.synthesizeFromYaml(folder);
        }

        // Case 2: launch.json entry with a target but no program — derive.
        if (config.type === 'koh' && !config.program) {
            if (!folder) {
                vscode.window.showErrorMessage('Koh debug configuration needs a workspace folder.');
                return undefined;
            }
            const yaml = await this.yamlReader.read(folder);
            if (!yaml) {
                vscode.window.showErrorMessage('Koh debug configuration references target but no koh.yaml found.');
                return undefined;
            }
            const targets = this.yamlReader.resolveTargets(yaml, folder);
            const target = targets.find(t => t.name === config.target) ?? targets[0];
            if (!target) {
                vscode.window.showErrorMessage('No Koh targets available.');
                return undefined;
            }
            config.program = target.romPath;
            config.debugInfo = config.debugInfo ?? target.kdbgPath;
            config.preLaunchTask = config.preLaunchTask ?? `koh: build ${target.name}`;
        }

        return config;
    }

    private async synthesizeFromYaml(
        folder: vscode.WorkspaceFolder | undefined
    ): Promise<KohLaunchConfiguration | undefined> {
        if (!folder) {
            vscode.window.showInformationMessage('Open a folder to debug Koh ROMs.');
            return undefined;
        }

        const yaml = await this.yamlReader.read(folder);
        if (!yaml || yaml.projects.length === 0) {
            const action = await vscode.window.showInformationMessage(
                'No koh.yaml found. Create one or add a launch.json configuration?',
                'Generate koh.yaml',
                'Open launch.json'
            );
            if (action === 'Generate koh.yaml') {
                await vscode.commands.executeCommand('koh.generateConfig');
            } else if (action === 'Open launch.json') {
                await vscode.commands.executeCommand('workbench.action.debug.configure');
            }
            return undefined;
        }

        const targets = this.yamlReader.resolveTargets(yaml, folder);
        const picked = await this.targetSelector.pick(targets);
        if (!picked) return undefined;

        return {
            type: 'koh',
            request: 'launch',
            name: `Debug ${picked.name}`,
            target: picked.name,
            program: picked.romPath,
            debugInfo: picked.kdbgPath,
            hardwareMode: 'auto',
            stopOnEntry: false,
            preLaunchTask: `koh: build ${picked.name}`,
        };
    }
}
