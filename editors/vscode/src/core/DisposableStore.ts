import * as vscode from 'vscode';

export class DisposableStore implements vscode.Disposable {
    private readonly items: vscode.Disposable[] = [];
    private disposed = false;

    add(d: vscode.Disposable): void {
        if (this.disposed) {
            d.dispose();
            return;
        }
        this.items.push(d);
    }

    addAll(ds: vscode.Disposable[]): void {
        for (const d of ds) this.add(d);
    }

    dispose(): void {
        if (this.disposed) return;
        this.disposed = true;
        for (const d of this.items.reverse()) {
            try { d.dispose(); } catch { /* swallow during shutdown */ }
        }
    }
}
