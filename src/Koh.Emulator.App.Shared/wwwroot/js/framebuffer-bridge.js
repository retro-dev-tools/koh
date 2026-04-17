// Allocates persistent ImageData + Uint8ClampedArray and exposes a single-copy
// commit path from WASM to the canvas. See §10.4 of the emulator design spec.
window.kohFramebufferBridge = (function () {
    const WIDTH = 160;
    const HEIGHT = 144;
    let imageData = null;
    let canvas = null;
    let ctx = null;

    return {
        attach: function (canvasId) {
            canvas = document.getElementById(canvasId);
            if (!canvas) throw new Error('Canvas not found: ' + canvasId);
            ctx = canvas.getContext('2d');
            imageData = ctx.createImageData(WIDTH, HEIGHT);
        },

        commit: function (base64Pixels) {
            if (!imageData || !ctx) return;
            const raw = atob(base64Pixels);
            const dst = imageData.data;
            for (let i = 0; i < raw.length; i++) {
                dst[i] = raw.charCodeAt(i);
            }
            ctx.putImageData(imageData, 0, 0);
        }
    };
})();
