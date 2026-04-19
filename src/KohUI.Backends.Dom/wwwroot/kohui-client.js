// KohUI DOM client — receives patches over WebSocket, applies them to
// the DOM, forwards DOM events back to the server.
//
// Protocol (server → client):
//   {op:"replace", path:"", node:{...}}            full tree (initial or subtree replace)
//   {op:"batch",   patches:[...]}                  one tick's worth of diffs
//     patches are one of:
//       {op:"replace", path, node}
//       {op:"props",   path, set:{...}, remove:[]}
//       {op:"insert",  path, index, node}
//       {op:"remove",  path, index}
//
// Protocol (client → server):
//   {op:"event", path:"0.1", event:"click"}
//
// Paths are dot-separated indices from root. Root is path "".

(function () {
    const ROOT_ID = "kohui-root";

    // ─── Element creation from RenderNode ────────────────────────────

    function createElement(node) {
        const { tag, className } = domSpec(node.type);
        const el = document.createElement(tag);
        if (className) el.classList.add(className);
        el.classList.add("kohui-" + node.type.toLowerCase());
        // Store the original pascal-case type verbatim so patch handlers
        // can key on it without guessing from lowercased class names —
        // "StatusBarSegment" ≠ "Statusbarsegment" when case matters.
        el.dataset.kohuiType = node.type;
        if (node.key) el.dataset.kohuiKey = node.key;

        // Windows wrap a header (.title-bar) + body (.window-body);
        // children of the C# Window become children of the body, not
        // the window itself.
        if (node.type === "Window") {
            const titleBar = buildWindowTitleBar(node);
            const body = document.createElement("div");
            body.classList.add("window-body", "kohui-window-body");
            el.appendChild(titleBar);
            el.appendChild(body);
            for (const child of (node.children || [])) {
                body.appendChild(createElement(child));
            }
            applyProps(el, node);
            return el;
        }

        applyProps(el, node);
        for (const child of (node.children || [])) {
            el.appendChild(createElement(child));
        }
        return el;
    }

    function domSpec(type) {
        switch (type) {
            case "Button":           return { tag: "button", className: "" };
            case "Label":            return { tag: "span",   className: "" };
            case "Stack":            return { tag: "div",    className: "" };
            case "Window":           return { tag: "div",    className: "window" };
            case "MenuBar":          return { tag: "ul",     className: "menu-bar" };
            case "MenuItem":         return { tag: "li",     className: "menu-bar-item" };
            case "Panel":            return { tag: "div",    className: "" };
            case "StatusBar":        return { tag: "div",    className: "status-bar" };
            case "StatusBarSegment": return { tag: "p",      className: "status-bar-field" };
            case "CheckBox":         return { tag: "label",  className: "" };
            case "RadioButton":      return { tag: "label",  className: "" };
            default:                 return { tag: "div",    className: "" };
        }
    }

    function buildWindowTitleBar(node) {
        const tb = document.createElement("div");
        tb.classList.add("title-bar");
        const text = document.createElement("div");
        text.classList.add("title-bar-text");
        text.textContent = (node.props && node.props.title) || "";
        tb.appendChild(text);

        const controls = document.createElement("div");
        controls.classList.add("title-bar-controls");
        if ((node.props || {}).onClose === true) {
            const closeBtn = document.createElement("button");
            closeBtn.setAttribute("aria-label", "Close");
            closeBtn.addEventListener("click", ev => {
                ev.stopPropagation();
                const owner = closeBtn.closest(".kohui-window");
                if (!owner) return;
                ws.send(JSON.stringify({ op: "event", path: pathOf(owner), event: "close" }));
            });
            controls.appendChild(closeBtn);
        }
        tb.appendChild(controls);

        makeDraggable(tb);
        return tb;
    }

    // Client-side drag: pointer events translate the window container
    // via CSS transforms. Position is never sent back to the server.
    //
    // Crucially, drag starts only when the pointer-down happens on
    // empty title-bar space — NOT on a control button. Earlier bug:
    // setPointerCapture on the whole title bar stole clicks from the
    // inline close button, so the close-button's click listener never
    // fired (the subsequent click event dispatched to the title bar
    // instead, because it owned pointer capture).
    function makeDraggable(titleBar) {
        let active = null;
        titleBar.addEventListener("pointerdown", e => {
            if (e.button !== 0) return;
            if (e.target instanceof Element && e.target.closest(".title-bar-controls")) return;
            const win = titleBar.closest(".kohui-window");
            if (!win) return;
            const rect = win.getBoundingClientRect();
            active = { win, dx: e.clientX - rect.left, dy: e.clientY - rect.top };
            titleBar.setPointerCapture(e.pointerId);
        });
        titleBar.addEventListener("pointermove", e => {
            if (!active) return;
            const x = e.clientX - active.dx;
            const y = e.clientY - active.dy;
            active.win.style.left = x + "px";
            active.win.style.top  = y + "px";
        });
        const end = e => {
            if (active) {
                try { titleBar.releasePointerCapture(e.pointerId); } catch {}
                active = null;
            }
        };
        titleBar.addEventListener("pointerup", end);
        titleBar.addEventListener("pointercancel", end);
    }

    function applyProps(el, node) {
        const p = node.props || {};
        switch (node.type) {
            case "Label":
                el.textContent = p.text ?? "";
                break;

            case "Button":
                el.textContent = p.text ?? "";
                el.disabled = p.enabled === false;
                wireEventForwarder(el, "click", p.onClick === true);
                break;

            case "Stack":
                el.classList.remove("kohui-stack-Vertical", "kohui-stack-Horizontal");
                el.classList.add("kohui-stack");
                el.classList.add("kohui-stack-" + (p.direction || "Vertical"));
                break;

            case "Window":
                el.style.position = "absolute";
                el.style.left  = (p.x ?? 40) + "px";
                el.style.top   = (p.y ?? 40) + "px";
                el.style.width = (p.width  ?? 320) + "px";
                // Let height auto-size to content; p.height is advisory.
                break;

            case "MenuItem":
                // Render accelerator ampersand as underline.
                const raw = p.text ?? "";
                el.innerHTML = renderAccelerator(raw);
                wireEventForwarder(el, "click", p.onClick === true);
                break;

            case "Panel":
                el.classList.remove("kohui-panel-Sunken", "kohui-panel-Raised", "kohui-panel-Chiseled", "sunken-panel");
                const bevel = p.bevel || "Sunken";
                el.classList.add("kohui-panel-" + bevel);
                if (bevel === "Sunken") el.classList.add("sunken-panel");
                break;

            case "StatusBarSegment":
                el.textContent = p.text ?? "";
                break;

            case "CheckBox":
            case "RadioButton": {
                // <label><input type=checkbox|radio> <span>text</span></label>
                // Rebuild each time — cheap for this cardinality, and keeps
                // the click forwarder wired to the <label>, which is the
                // natural hit target for both the box and the text.
                el.replaceChildren();
                const input = document.createElement("input");
                input.type = node.type === "CheckBox" ? "checkbox" : "radio";
                input.checked = node.type === "CheckBox" ? (p.checked === true) : (p.selected === true);
                // Intercept the native change so the MVU loop stays the
                // source of truth — no flicker if the dispatch is a no-op.
                input.addEventListener("click", ev => ev.preventDefault());
                el.appendChild(input);
                const span = document.createElement("span");
                span.textContent = " " + (p.text ?? "");
                el.appendChild(span);
                wireEventForwarder(el, "click", p.onClick === true);
                break;
            }
        }
    }

    function renderAccelerator(text) {
        const i = text.indexOf("&");
        if (i < 0 || i >= text.length - 1) return escapeHtml(text);
        return escapeHtml(text.slice(0, i))
             + "<u>" + escapeHtml(text[i + 1]) + "</u>"
             + escapeHtml(text.slice(i + 2));
    }

    function escapeHtml(s) {
        return s.replace(/[&<>"']/g, c => ({
            "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
        })[c]);
    }

    function wireEventForwarder(el, domEventName, hasHandler) {
        const marker = "_kohuiHas_" + domEventName;
        if (!hasHandler) {
            if (el[marker]) {
                el.removeEventListener(domEventName, el[marker]);
                el[marker] = null;
            }
            return;
        }
        if (el[marker]) return;   // already wired
        const listener = () => {
            const path = pathOf(el);
            ws.send(JSON.stringify({ op: "event", path, event: domEventName }));
        };
        el.addEventListener(domEventName, listener);
        el[marker] = listener;
    }

    // ─── Patch application ───────────────────────────────────────────

    function applyPatch(patch) {
        switch (patch.op) {
            case "replace": {
                const target = elementAt(patch.path);
                const fresh = createElement(patch.node);
                if (patch.path === "") {
                    document.getElementById(ROOT_ID).replaceChildren(fresh);
                } else if (target) {
                    target.replaceWith(fresh);
                }
                setPathDataAttrs(document.getElementById(ROOT_ID).firstElementChild, "");
                break;
            }
            case "props": {
                const target = elementAt(patch.path);
                if (!target) return;
                const synthetic = {
                    type: typeOf(target),
                    props: { ...readProps(target), ...patch.set },
                };
                for (const removed of (patch.remove || [])) delete synthetic.props[removed];
                applyProps(target, synthetic);
                break;
            }
            case "insert": {
                const parent = childContainerOf(elementAt(patch.path));
                if (!parent) return;
                const child = createElement(patch.node);
                if (patch.index >= parent.children.length) parent.appendChild(child);
                else parent.insertBefore(child, parent.children[patch.index]);
                setPathDataAttrs(document.getElementById(ROOT_ID).firstElementChild, "");
                break;
            }
            case "remove": {
                const parent = childContainerOf(elementAt(patch.path));
                if (!parent) return;
                const victim = parent.children[patch.index];
                if (victim) parent.removeChild(victim);
                setPathDataAttrs(document.getElementById(ROOT_ID).firstElementChild, "");
                break;
            }
        }
    }

    // For compound widgets like Window (which own a title-bar + body
    // pair), the "children" addressed by the server live in the body
    // slot. Everything else uses the element itself.
    function childContainerOf(el) {
        if (!el) return null;
        if (el.classList.contains("kohui-window")) return el.querySelector(":scope > .kohui-window-body");
        return el;
    }

    function elementAt(path) {
        const root = document.getElementById(ROOT_ID);
        if (path === "") return root.firstElementChild;
        let node = root.firstElementChild;
        for (const seg of path.split(".")) {
            const i = parseInt(seg, 10);
            const container = childContainerOf(node);
            node = container && container.children[i];
        }
        return node;
    }

    function pathOf(el) {
        const segs = [];
        let n = el;
        while (n && n.parentElement && n.parentElement.id !== ROOT_ID) {
            const parent = n.parentElement;
            // Look past the .window-body wrapper so paths match the
            // server's tree shape (Window's children are its body's
            // children, not the title-bar/body pair).
            const addressableParent = parent.classList.contains("kohui-window-body")
                ? parent.parentElement
                : parent;
            const siblings = parent.children;
            segs.push(Array.prototype.indexOf.call(siblings, n).toString());
            n = addressableParent;
        }
        segs.reverse();
        return segs.join(".");
    }

    function setPathDataAttrs(el, path) {
        if (!el) return;
        el.dataset.kohuiPath = path;
        const body = childContainerOf(el);
        const children = body === el ? el.children : body ? body.children : [];
        for (let i = 0; i < children.length; i++) {
            setPathDataAttrs(children[i], path === "" ? i.toString() : `${path}.${i}`);
        }
    }

    function typeOf(el) {
        return (el && el.dataset && el.dataset.kohuiType) || "";
    }

    function readProps(el) {
        const props = {};
        const type = typeOf(el);
        if (type === "Label" || type === "Button" || type === "StatusBarSegment") props.text = el.textContent;
        if (type === "Button") props.enabled = !el.disabled;
        if (type === "CheckBox" || type === "RadioButton") {
            // The label's textContent concatenates input (empty) + span (" "+text).
            // Strip the leading space so round-trip preserves the server's view.
            const span = el.querySelector(":scope > span");
            if (span) props.text = span.textContent.replace(/^ /, "");
            const input = el.querySelector(":scope > input");
            if (input) {
                if (type === "CheckBox")    props.checked = input.checked;
                if (type === "RadioButton") props.selected = input.checked;
            }
        }
        return props;
    }

    // ─── WebSocket plumbing ──────────────────────────────────────────

    let ws = null;
    function connect() {
        const proto = location.protocol === "https:" ? "wss:" : "ws:";
        ws = new WebSocket(`${proto}//${location.host}/_kohui/ws`);
        ws.onmessage = ev => {
            const msg = JSON.parse(ev.data);
            if (msg.op === "batch") for (const p of msg.patches) applyPatch(p);
            else applyPatch(msg);
        };
        ws.onclose = () => setTimeout(connect, 500);   // auto-reconnect during dev
        ws.onerror = () => { /* onclose runs next */ };
    }

    // kohui-client.js is loaded at the end of <body>, so DOMContentLoaded
    // has typically fired by the time this script parses. Queue connect()
    // immediately in that case; only defer if we're somehow earlier in
    // the load cycle.
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", connect);
    } else {
        connect();
    }
})();
