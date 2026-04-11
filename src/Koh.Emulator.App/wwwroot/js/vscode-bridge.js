// Bridges postMessage from the VS Code extension to the Blazor app.
window.__kohVsCodeBridge = true;

(function () {
    let vsCodeApi = null;
    try {
        if (typeof acquireVsCodeApi === 'function') {
            vsCodeApi = acquireVsCodeApi();
        }
    } catch (e) {
        // acquireVsCodeApi throws if called twice; ignore.
    }

    let blazorHandler = null;

    window.kohVsCodeBridge = {
        register: function (dotNetObjRef) {
            blazorHandler = dotNetObjRef;
        },

        sendToExtension: function (kind, payload) {
            if (vsCodeApi) {
                vsCodeApi.postMessage({ kind: kind, payload: payload });
            }
        }
    };

    window.addEventListener('message', function (event) {
        const data = event.data;
        if (!data || !blazorHandler) return;
        if (data.kind === 'dap') {
            blazorHandler.invokeMethodAsync('ReceiveDap', data.payload);
        }
    });
})();
