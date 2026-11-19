import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { deactivate } from '../extension';
import { executableName } from '../toolchain/paths';
import { Logger } from '../core/Logger';
import { LspClientManager } from '../lsp/LspClientManager';
import { ToolchainResolver } from '../toolchain/ToolchainResolver';

async function waitForDiagnostics(uri: vscode.Uri, hasErrors: boolean): Promise<void> {
    const deadline = Date.now() + 15_000;
    while (Date.now() < deadline) {
        const errors = vscode.languages.getDiagnostics(uri).filter(d => d.source === 'koh' && d.severity === vscode.DiagnosticSeverity.Error);
        if ((errors.length > 0) === hasErrors) return;
        await new Promise(resolve => setTimeout(resolve, 25));
    }
    assert.fail(`diagnostics did not reach hasErrors=${hasErrors}`);
}

async function waitForConstant(uri: vscode.Uri, present: boolean): Promise<void> {
    const deadline = Date.now() + 15_000;
    while (Date.now() < deadline) {
        const result = await vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', uri, new vscode.Position(9, 6));
        if (!!result?.items.some(item => item.label === 'VALUE') === present) return;
        await new Promise(resolve => setTimeout(resolve, 25));
    }
    assert.fail(`configured completion did not reach present=${present}`);
}

suite('Packaged native language server', () => {
    test('failed native launch is reported without an unhandled process error', async () => {
        const log = new Logger('Koh launch failure test');
        const messages: string[] = [];
        log.error = message => messages.push(message);
        log.show = () => {};
        const toolchain = { resolve: () => ({ binDir: path.join(process.cwd(), 'missing-native-server'), source: 'settingOverride', version: 'test' }) } as ToolchainResolver;
        const manager = new LspClientManager(log, toolchain);
        try {
            await manager.start();
            assert.ok(messages.some(message => message.includes('ENOENT')));
        } finally {
            await manager.dispose();
            log.dispose();
        }
    });
    test('actual extension provides editor features and updates unsaved diagnostics', async function () {
        this.timeout(60_000);
        const bin = process.env.KOH_TEST_LSP_DIR;
        if (!bin) { this.skip(); return; }
        for (const name of ['koh-lsp', 'koh-asm']) assert.ok(fs.existsSync(path.join(bin, executableName(name))), name);
        const config = vscode.workspace.getConfiguration('koh');
        const oldPath = config.inspect<string>('toolchainPath')?.globalValue;
        const oldUpdate = config.inspect<string>('toolchain.updateCheck')?.globalValue;
        const folder = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        assert.ok(folder, 'native LSP tests require the disposable test workspace');
        const file = path.join(folder, 'main # 😀.asm');
        const source = 'SECTION "Main",ROM0\nVALUE EQU 42\nMACRO load_pair\n ld a,\\1\n ld b,\\2\nENDM\nStart:\n nop\n jp Start\n ld a,VALUE\n load_pair 1,2\n';
        fs.writeFileSync(file, source);
        const uri = vscode.Uri.file(file);
        try {
            await config.update('toolchainPath', bin, vscode.ConfigurationTarget.Global);
            await config.update('toolchain.updateCheck', 'never', vscode.ConfigurationTarget.Global);
            const extension = vscode.extensions.getExtension('retro-dev-tools.koh-asm');
            assert.ok(extension, 'development extension must be loaded');
            await extension.activate();
            const document = await vscode.workspace.openTextDocument(uri);
            assert.strictEqual(document.languageId, 'koh-asm');
            await vscode.window.showTextDocument(document);
            const reference = new vscode.Position(8, 5);
            const hovers = await vscode.commands.executeCommand<vscode.Hover[]>('vscode.executeHoverProvider', uri, reference);
            assert.ok(hovers?.some(hover => hover.contents.some(content => (typeof content === 'string' ? content : content.value).includes('**Start**'))));
            const definitions = await vscode.commands.executeCommand<(vscode.Location | vscode.LocationLink)[]>('vscode.executeDefinitionProvider', uri, reference);
            assert.ok(definitions?.some(location => {
                const target = 'targetUri' in location ? location.targetUri : location.uri;
                const range = 'targetRange' in location ? location.targetRange : location.range;
                return target.toString() === uri.toString() && range.start.line === 6;
            }));
            const references = await vscode.commands.executeCommand<vscode.Location[]>('vscode.executeReferenceProvider', uri, reference);
            assert.ok(references?.some(location => location.range.start.line === 8));
            const outline = await vscode.commands.executeCommand<vscode.DocumentSymbol[]>('vscode.executeDocumentSymbolProvider', uri);
            assert.ok(outline?.some(symbol => symbol.name === 'Start'));
            const completion = await vscode.commands.executeCommand<vscode.CompletionList>('vscode.executeCompletionItemProvider', uri, new vscode.Position(9, 6));
            assert.ok(completion?.items.some(item => item.label === 'VALUE'));
            const rename = await vscode.commands.executeCommand<vscode.WorkspaceEdit>('vscode.executeDocumentRenameProvider', uri, reference, 'Renamed');
            assert.strictEqual(rename?.get(uri).length, 2);
            assert.ok(rename.get(uri).every(edit => edit.newText === 'Renamed'));
            const hints = await vscode.commands.executeCommand<vscode.InlayHint[]>('vscode.executeInlayHintProvider', uri, new vscode.Range(0, 0, 11, 0));
            assert.ok(hints?.some(hint => typeof hint.label === 'string' && hint.label.includes('$002A (42)')));
            const signature = await vscode.commands.executeCommand<vscode.SignatureHelp>('vscode.executeSignatureHelpProvider', uri, new vscode.Position(10, 13), ',');
            assert.strictEqual(signature?.signatures[0].parameters.length, 2);
            assert.strictEqual(signature.activeParameter, 1);
            const tokens = await vscode.commands.executeCommand<vscode.SemanticTokens>('vscode.provideDocumentSemanticTokens', uri);
            assert.ok(tokens?.data.length);
            const edit = new vscode.WorkspaceEdit();
            edit.replace(uri, new vscode.Range(0, 0, document.lineCount, 0), 'ld a,\n');
            assert.ok(await vscode.workspace.applyEdit(edit));
            await waitForDiagnostics(uri, true);
            const fix = new vscode.WorkspaceEdit();
            fix.replace(uri, new vscode.Range(0, 0, document.lineCount, 0), source);
            assert.ok(await vscode.workspace.applyEdit(fix));
            await waitForDiagnostics(uri, false);
            assert.ok(await document.save());
            const yamlUri = vscode.Uri.file(path.join(folder, 'koh.yaml'));
            fs.writeFileSync(yamlUri.fsPath, 'version: 1\nprojects: []\n');
            const yaml = await vscode.workspace.openTextDocument(yamlUri);
            const changeConfig = new vscode.WorkspaceEdit();
            changeConfig.replace(yamlUri, new vscode.Range(0, 0, yaml.lineCount, 0), 'version: 1\nprojects: [{name: game, entrypoint: missing.asm}]\n');
            assert.ok(await vscode.workspace.applyEdit(changeConfig));
            assert.ok(await yaml.save());
            await waitForConstant(uri, false);
            const restoreConfig = new vscode.WorkspaceEdit();
            restoreConfig.replace(yamlUri, new vscode.Range(0, 0, yaml.lineCount, 0), `version: 1\nprojects: [{name: game, entrypoint: '${path.basename(file)}'}]\n`);
            assert.ok(await vscode.workspace.applyEdit(restoreConfig));
            assert.ok(await yaml.save());
            await waitForConstant(uri, true);
            console.log('Native LSP: editor assertions complete');
        } finally {
            await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
            await deactivate();
            await config.update('toolchainPath', oldPath, vscode.ConfigurationTarget.Global);
            await config.update('toolchain.updateCheck', oldUpdate, vscode.ConfigurationTarget.Global);
        }
    });
});
