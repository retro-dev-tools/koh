import * as vscode from 'vscode';
import { DisposableStore } from './DisposableStore';
import { Logger } from './Logger';
import { LspClientManager } from '../lsp/LspClientManager';
import { KohYamlReader } from '../config/KohYamlReader';
import { BuildTaskProvider } from '../build/BuildTaskProvider';
import { generateConfigCommand, maybePromptGenerateConfig } from '../commands/GenerateConfigCommand';

export class KohExtension {
    private readonly disposables = new DisposableStore();
    private readonly log: Logger;
    private readonly yamlReader: KohYamlReader;
    private readonly lsp: LspClientManager;
    private readonly buildTasks: BuildTaskProvider;

    constructor(private readonly context: vscode.ExtensionContext) {
        this.log = new Logger('Koh');
        this.disposables.add(this.log);
        this.yamlReader = new KohYamlReader(this.log);
        this.lsp = new LspClientManager(this.log);
        this.buildTasks = new BuildTaskProvider(this.log, this.yamlReader);
    }

    async start(): Promise<void> {
        this.log.info('Koh extension activating...');

        this.disposables.add(
            vscode.commands.registerCommand('koh.generateConfig', () => generateConfigCommand(this.log)),
        );

        await this.lsp.start();
        this.disposables.add(this.lsp);

        this.disposables.add(this.buildTasks.register());

        await maybePromptGenerateConfig(this.log);
    }

    async dispose(): Promise<void> {
        this.disposables.dispose();
    }
}
