import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { KohYamlReader } from '../config/KohYamlReader';
import { KohConfigurationProvider } from './ConfigurationProvider';
import { TargetSelector } from './TargetSelector';
import { KohInlineDapAdapter } from './InlineDapAdapter';
import { DapMessageQueue } from './DapMessageQueue';
import { EmulatorPanelHost } from '../webview/EmulatorPanelHost';

export class KohDebugRegistration {
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly log: Logger,
        private readonly yamlReader: KohYamlReader,
        private readonly panelHost: EmulatorPanelHost
    ) {}

    register(): vscode.Disposable {
        const disposables: vscode.Disposable[] = [];

        const configProvider = new KohConfigurationProvider(this.log, this.yamlReader, new TargetSelector());
        disposables.push(vscode.debug.registerDebugConfigurationProvider('koh', configProvider));

        disposables.push(vscode.debug.registerDebugAdapterDescriptorFactory('koh', {
            createDebugAdapterDescriptor: (session) => {
                const panel = this.panelHost.openForSession(session);
                const queue = new DapMessageQueue();
                const adapter = new KohInlineDapAdapter(
                    msg => panel.postToWebview(msg),
                    queue,
                    () => panel.dispose()
                );
                panel.onMessageFromWebview((m: { kind: string; payload?: unknown }) => {
                    if (m.kind === 'ready') {
                        queue.markReady((msg: unknown) => panel.postToWebview(msg));
                    } else if (m.kind === 'dap') {
                        adapter.receiveFromWebview(m.payload);
                    }
                });
                return new vscode.DebugAdapterInlineImplementation(adapter);
            }
        }));

        return vscode.Disposable.from(...disposables);
    }
}
