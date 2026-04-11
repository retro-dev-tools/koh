import * as vscode from 'vscode';

export class Logger {
    private readonly channel: vscode.LogOutputChannel;

    constructor(name: string) {
        this.channel = vscode.window.createOutputChannel(name, { log: true });
    }

    info(msg: string): void { this.channel.info(msg); }
    warn(msg: string): void { this.channel.warn(msg); }
    error(msg: string): void { this.channel.error(msg); }
    show(): void { this.channel.show(true); }

    dispose(): void { this.channel.dispose(); }
}
