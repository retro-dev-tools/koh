import * as vscode from 'vscode';
import { DapMessageQueue } from './DapMessageQueue';

export type WebviewPostMessage = (msg: unknown) => void;

export class KohInlineDapAdapter implements vscode.DebugAdapter {
    private readonly messageEmitter = new vscode.EventEmitter<vscode.DebugProtocolMessage>();
    readonly onDidSendMessage = this.messageEmitter.event;

    constructor(
        private readonly postToWebview: WebviewPostMessage,
        private readonly queue: DapMessageQueue,
        private readonly onDispose: () => void
    ) {}

    handleMessage(message: vscode.DebugProtocolMessage): void {
        this.queue.enqueueOutbound(message, msg => this.postToWebview({ kind: 'dap', payload: msg }));
    }

    receiveFromWebview(payload: unknown): void {
        this.messageEmitter.fire(payload as vscode.DebugProtocolMessage);
    }

    dispose(): void {
        this.messageEmitter.dispose();
        this.onDispose();
    }
}
