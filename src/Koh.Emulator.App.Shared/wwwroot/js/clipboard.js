// Clipboard helper used by the "Snapshot" button. Prefers the async
// navigator.clipboard API, falls back to a hidden <textarea> + execCommand
// for WebView2 contexts where the permission may not be granted.
window.kohClipboard = (function () {
    async function write(text) {
        try {
            if (navigator.clipboard && navigator.clipboard.writeText) {
                await navigator.clipboard.writeText(text);
                return true;
            }
        } catch (_) { /* fall through */ }

        // Fallback path.
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.left = '-9999px';
        document.body.appendChild(ta);
        ta.select();
        let ok = false;
        try { ok = document.execCommand('copy'); } catch (_) { ok = false; }
        document.body.removeChild(ta);
        return ok;
    }

    return { write };
})();
