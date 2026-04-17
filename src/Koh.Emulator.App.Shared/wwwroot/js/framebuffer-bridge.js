// Persistent ImageData allocated once; each frame copies straight from a
// Uint8Array handed over by Blazor's byte[] marshalling — no base64 hop.
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

        commit: function (pixels) {
            if (!imageData || !ctx) return;
            // `pixels` arrives as a Uint8Array (from Blazor byte[] marshalling)
            // or similar array-like; copy into the persistent ImageData buffer.
            imageData.data.set(pixels);
            ctx.putImageData(imageData, 0, 0);
        }
    };
})();
