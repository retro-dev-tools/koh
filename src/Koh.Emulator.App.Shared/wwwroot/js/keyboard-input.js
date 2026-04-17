window.kohKeyboard = (function () {
    let handler = null;
    const pressed = new Set();

    function dispatch(key, isDown) {
        if (!handler) return;
        if (isDown && pressed.has(key)) return;
        if (isDown) pressed.add(key); else pressed.delete(key);
        handler.invokeMethodAsync(isDown ? "OnKeyDown" : "OnKeyUp", key);
    }

    return {
        register: function (dotNetObjRef) {
            handler = dotNetObjRef;
            window.addEventListener("keydown", (e) => {
                if (shouldCapture(e.code)) { dispatch(e.code, true); e.preventDefault(); }
            });
            window.addEventListener("keyup", (e) => {
                if (shouldCapture(e.code)) { dispatch(e.code, false); e.preventDefault(); }
            });
        },
    };

    function shouldCapture(code) {
        switch (code) {
            case "ArrowUp": case "ArrowDown": case "ArrowLeft": case "ArrowRight":
            case "KeyZ": case "KeyX": case "Enter": case "ShiftRight":
                return true;
            default:
                return false;
        }
    }
})();
