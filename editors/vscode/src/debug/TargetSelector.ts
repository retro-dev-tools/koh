import * as vscode from 'vscode';
import { ResolvedTarget } from '../config/WorkspaceConfig';

export class TargetSelector {
    async pick(targets: ResolvedTarget[]): Promise<ResolvedTarget | undefined> {
        if (targets.length === 0) return undefined;
        if (targets.length === 1) return targets[0];

        const picked = await vscode.window.showQuickPick(
            targets.map(t => ({ label: t.name, description: t.romPath, target: t })),
            { placeHolder: 'Select Koh target to debug' }
        );
        return picked?.target;
    }
}
