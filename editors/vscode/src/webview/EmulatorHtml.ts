import * as vscode from 'vscode';
import { BlazorAssetSource } from './BlazorAssetLoader';

export function buildEmulatorHtml(webview: vscode.Webview, assets: BlazorAssetSource): string {
    const cspSources = [
        `default-src 'none'`,
        `script-src ${webview.cspSource} 'unsafe-inline' 'unsafe-eval' ${assets.cspSources.join(' ')}`,
        `style-src ${webview.cspSource} 'unsafe-inline' ${assets.cspSources.join(' ')}`,
        `connect-src ${webview.cspSource} ${assets.cspSources.join(' ')}`,
        `img-src ${webview.cspSource} data: ${assets.cspSources.join(' ')}`,
        `font-src ${webview.cspSource} ${assets.cspSources.join(' ')}`,
    ].join('; ');

    const baseHref = typeof assets.baseUri === 'string' ? assets.baseUri + '/' : assets.baseUri.toString() + '/';

    return `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta http-equiv="Content-Security-Policy" content="${cspSources}" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="${baseHref}" />
    <title>Koh Emulator</title>
    <link href="css/emulator.css" rel="stylesheet" />
</head>
<body>
    <div id="app">Loading Blazor runtime…</div>
    <div id="blazor-error-ui" style="display:none">
        An unhandled error has occurred.
        <a href="" class="reload">Reload</a>
        <a class="dismiss">🗙</a>
    </div>
    <script src="js/runtime-mode.js"></script>
    <script src="js/frame-pacer.js"></script>
    <script src="js/vscode-bridge.js"></script>
    <script src="_framework/blazor.webassembly.js"></script>
</body>
</html>`;
}
