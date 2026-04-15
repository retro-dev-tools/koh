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

    const pendingFileReads = new Map();
    let fileReadSeq = 0;

    window.kohVsCodeBridge = {
        register: function (dotNetObjRef) {
            blazorHandler = dotNetObjRef;
        },

        sendToExtension: function (kind, payload) {
            if (vsCodeApi) {
                vsCodeApi.postMessage({ kind: kind, payload: payload });
            }
        },

        requestFile: function (path) {
            return new Promise(function (resolve) {
                const id = ++fileReadSeq;
                pendingFileReads.set(id, resolve);
                if (vsCodeApi) {
                    vsCodeApi.postMessage({ kind: 'readFile', id: id, path: path });
                } else {
                    pendingFileReads.delete(id);
                    resolve(null);
                }
            });
        },

        resolveFile: function (id, base64Data) {
            const resolve = pendingFileReads.get(id);
            if (resolve) {
                pendingFileReads.delete(id);
                resolve(base64Data);
            }
        }
    };

    window.addEventListener('message', function (event) {
        const data = event.data;
        if (!data) return;
        if (data.kind === 'fileData') {
            window.kohVsCodeBridge.resolveFile(data.id, data.base64);
            return;
        }
        if (!blazorHandler) return;
        if (data.kind === 'dap') {
            blazorHandler.invokeMethodAsync('ReceiveDap', data.payload);
        }
    });
})();
