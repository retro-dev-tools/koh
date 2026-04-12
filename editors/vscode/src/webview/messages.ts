export type ExtensionToWebviewMessage =
    | { kind: 'dap'; payload: unknown }
    | { kind: 'fileData'; id: number; base64: string | null };

export type WebviewToExtensionMessage =
    | { kind: 'ready' }
    | { kind: 'dap'; payload: unknown }
    | { kind: 'readFile'; id: number; path: string }
    | { kind: 'fatalError'; message: string; stack?: string };
