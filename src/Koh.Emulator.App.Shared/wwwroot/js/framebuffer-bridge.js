// Persistent ImageData allocated once; each frame copies straight from a
// Uint8Array handed over by Blazor's byte[] marshalling — no base64 hop.
window.kohFramebufferBridge = (function () {
    const WIDTH = 160;
    const HEIGHT = 144;
    let imageData = null;
    let canvas = null;
    let ctx = null;
    let rafRef = null;
    let rafHandle = 0;

    function tick() {
        if (!rafRef) return;
        rafRef.invokeMethodAsync('OnRaf');
        rafHandle = requestAnimationFrame(tick);
    }

    return {
        attach: function (canvasId) {
            canvas = document.getElementById(canvasId);
            if (!canvas) throw new Error('Canvas not found: ' + canvasId);
            ctx = canvas.getContext('2d');
            imageData = ctx.createImageData(WIDTH, HEIGHT);
        },

        commit: function (pixels) {
            if (!imageData || !ctx) return;
            imageData.data.set(pixels);
            ctx.putImageData(imageData, 0, 0);
        },

        startRafLoop: function (dotNetRef) {
            rafRef = dotNetRef;
            if (!rafHandle) rafHandle = requestAnimationFrame(tick);
        },

        stopRafLoop: function () {
            rafRef = null;
            if (rafHandle) { cancelAnimationFrame(rafHandle); rafHandle = 0; }
        },
    };
})();
