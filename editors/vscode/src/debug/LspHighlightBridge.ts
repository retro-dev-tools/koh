import * as vscode from 'vscode';
import { Logger } from '../core/Logger';

/**
 * Overlays a "current PC" highlight on the source line of the active stack
 * frame during a Koh debug session. VS Code already highlights the current
 * frame natively, but only while paused and only in the active editor; this
 * bridge extends the highlight to every visible editor so that split views
 * and preview editors all show where execution is.
 *
 * The bridge listens for active-stack-item changes published by the VS Code
 * debug API, resolves the frame source, and applies a whole-line decoration.
 * No custom DAP event is required.
 */
export class LspHighlightBridge implements vscode.Disposable {
    private readonly disposables: vscode.Disposable[] = [];
    private readonly decorationType: vscode.TextEditorDecorationType;
    private currentSession: vscode.DebugSession | undefined;

    constructor(private readonly log: Logger) {
        this.decorationType = vscode.window.createTextEditorDecorationType({
            backgroundColor: new vscode.ThemeColor('editor.stackFrameHighlightBackground'),
            isWholeLine: true,
            overviewRulerColor: new vscode.ThemeColor('editorOverviewRuler.stackFrameForeground'),
            overviewRulerLane: vscode.OverviewRulerLane.Full,
        });

        this.disposables.push(
            vscode.debug.onDidStartDebugSession(s => this.onStart(s)),
            vscode.debug.onDidTerminateDebugSession(s => this.onEnd(s)),
            vscode.debug.onDidChangeActiveStackItem(item => this.onActiveStackItemChanged(item)),
            vscode.window.onDidChangeVisibleTextEditors(() => this.refreshAll()),
        );
    }

    private onStart(session: vscode.DebugSession): void {
        if (session.type !== 'koh') return;
        this.currentSession = session;
        this.log.info(`LspHighlightBridge: session ${session.id} started`);
    }

    private onEnd(session: vscode.DebugSession): void {
        if (session.id !== this.currentSession?.id) return;
        this.currentSession = undefined;
        this.clearAll();
    }

    private async onActiveStackItemChanged(item: vscode.DebugThread | vscode.DebugStackFrame | undefined): Promise<void> {
        if (!this.currentSession) return;
        if (item === undefined || item instanceof vscode.DebugThread) {
            this.clearAll();
            return;
        }

        try {
            const frames = await this.currentSession.customRequest('stackTrace', {
                threadId: item.threadId,
                startFrame: item.frameId,
                levels: 1,
            });
            const frame = frames?.stackFrames?.[0];
            const source = frame?.source;
            if (!source?.path || typeof frame.line !== 'number') {
                this.clearAll();
                return;
            }
            this.highlight(source.path, frame.line);
        }
        catch (err) {
            this.log.warn(`LspHighlightBridge: stackTrace request failed: ${(err as Error).message}`);
        }
    }

    private refreshAll(): void {
        // Visible editors may have changed; re-running onActiveStackItemChanged
        // is not possible without an active stack item reference. Keep the
        // decoration on editors whose document we previously highlighted.
        // No-op unless we cached the last highlight — tracked below.
        if (this.lastPath && this.lastLine !== undefined) {
            this.highlight(this.lastPath, this.lastLine);
        }
    }

    private lastPath: string | undefined;
    private lastLine: number | undefined;

    private highlight(filePath: string, line: number): void {
        this.lastPath = filePath;
        this.lastLine = line;
        const normalized = vscode.Uri.file(filePath).fsPath.toLowerCase();
        const range = new vscode.Range(line - 1, 0, line - 1, 0);
        for (const editor of vscode.window.visibleTextEditors) {
            if (editor.document.uri.fsPath.toLowerCase() === normalized) {
                editor.setDecorations(this.decorationType, [range]);
            }
            else {
                editor.setDecorations(this.decorationType, []);
            }
        }
    }

    private clearAll(): void {
        this.lastPath = undefined;
        this.lastLine = undefined;
        for (const editor of vscode.window.visibleTextEditors) {
            editor.setDecorations(this.decorationType, []);
        }
    }

    dispose(): void {
        this.clearAll();
        this.decorationType.dispose();
        this.disposables.forEach(d => d.dispose());
    }
}
