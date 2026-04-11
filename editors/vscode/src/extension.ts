import * as vscode from 'vscode';
import { KohExtension } from './core/KohExtension';

let extension: KohExtension | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    extension = new KohExtension(context);
    await extension.start();
}

export async function deactivate(): Promise<void> {
    await extension?.dispose();
    extension = undefined;
}
