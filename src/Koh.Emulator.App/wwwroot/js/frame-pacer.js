window.kohFramePacer = {
    waitForRaf: function () {
        return new Promise(function (resolve) {
            window.requestAnimationFrame(function () { resolve(); });
        });
    }
};
