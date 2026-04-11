export type ExtensionToWebviewMessage =
    | { kind: 'dap'; payload: unknown };

export type WebviewToExtensionMessage =
    | { kind: 'ready' }
    | { kind: 'dap'; payload: unknown }
    | { kind: 'fatalError'; message: string; stack?: string };
