import * as vscode from 'vscode';

export class Logger {
    readonly outputChannel: vscode.LogOutputChannel;

    constructor(name: string) {
        this.outputChannel = vscode.window.createOutputChannel(name, { log: true });
    }

    info(msg: string): void { this.outputChannel.info(msg); }
    warn(msg: string): void { this.outputChannel.warn(msg); }
    error(msg: string): void { this.outputChannel.error(msg); }
    show(): void { this.outputChannel.show(true); }

    dispose(): void { this.outputChannel.dispose(); }
}
