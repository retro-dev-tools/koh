// Detects whether the Blazor app is running inside a VS Code webview.
window.kohRuntimeMode = {
    isInsideVsCodeWebview: function () {
        return typeof window.acquireVsCodeApi === 'function' || window.__kohVsCodeBridge === true;
    }
};
