// KohUI DOM client — receives patches over WebSocket, applies them to
// the DOM, forwards DOM events back to the server.
//
// Protocol (server → client):
//   {op:"replace", path:"", node:{...}}            // full tree (initial or subtree replace)
//   {op:"batch",   patches:[...]}                  // one tick's worth of diffs
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
        const tag = tagFor(node.type);
        const el = document.createElement(tag);

        // Stable class hooks + data attribute so Playwright and
        // inspectors can target nodes without relying on positions.
        el.classList.add("kohui-" + node.type.toLowerCase());
        if (node.key) el.dataset.kohuiKey = node.key;

        applyProps(el, node);

        if (node.children) {
            for (const child of node.children) el.appendChild(createElement(child));
        }
        return el;
    }

    function tagFor(type) {
        switch (type) {
            case "Button": return "button";
            case "Label":  return "span";
            case "Stack":  return "div";
            default:       return "div";
        }
    }

    function applyProps(el, node) {
        const p = node.props || {};
        switch (node.type) {
            case "Label":  el.textContent = p.text ?? ""; break;
            case "Button":
                el.textContent = p.text ?? "";
                el.disabled = p.enabled === false;
                wireEventForwarder(el, "click", node);
                break;
            case "Stack":
                el.classList.remove("kohui-stack-Vertical", "kohui-stack-Horizontal");
                el.classList.add("kohui-stack");
                el.classList.add("kohui-stack-" + (p.direction || "Vertical"));
                break;
        }
    }

    function wireEventForwarder(el, domEventName, node) {
        const propName = "on" + domEventName[0].toUpperCase() + domEventName.slice(1);
        const hasHandler = (node.props || {})[propName] === true;
        if (!hasHandler) return;
        el.addEventListener(domEventName, () => {
            const path = pathOf(el);
            ws.send(JSON.stringify({ op: "event", path, event: domEventName }));
        });
    }

    // ─── Patch application ───────────────────────────────────────────

    function applyPatch(patch) {
        switch (patch.op) {
            case "replace": {
                const target = elementAt(patch.path);
                const fresh = createElement(patch.node);
                // Path data-attribute is applied to the new element after
                // it's in place — see setPathDataAttrs.
                if (patch.path === "") {
                    document.getElementById(ROOT_ID).replaceChildren(fresh);
                } else {
                    target.replaceWith(fresh);
                }
                setPathDataAttrs(document.getElementById(ROOT_ID).firstElementChild, "");
                break;
            }
            case "props": {
                const target = elementAt(patch.path);
                if (!target) return;
                // Reconstruct a synthetic "node" with merged props so we
                // can reuse applyProps. For correctness we'd track the
                // current props in a shadow tree; for v0.1 the set of
                // widgets is small and applying the changed set directly
                // is good enough.
                const synthetic = {
                    type: typeOf(target),
                    props: { ...readProps(target), ...patch.set },
                };
                for (const removed of patch.remove) delete synthetic.props[removed];
                applyProps(target, synthetic);
                break;
            }
            case "insert": {
                const parent = elementAt(patch.path);
                const child = createElement(patch.node);
                if (patch.index >= parent.children.length) parent.appendChild(child);
                else parent.insertBefore(child, parent.children[patch.index]);
                setPathDataAttrs(document.getElementById(ROOT_ID).firstElementChild, "");
                break;
            }
            case "remove": {
                const parent = elementAt(patch.path);
                const victim = parent.children[patch.index];
                if (victim) parent.removeChild(victim);
                setPathDataAttrs(document.getElementById(ROOT_ID).firstElementChild, "");
                break;
            }
        }
    }

    function elementAt(path) {
        const root = document.getElementById(ROOT_ID);
        if (path === "") return root.firstElementChild;
        let node = root.firstElementChild;
        for (const seg of path.split(".")) {
            const i = parseInt(seg, 10);
            node = node && node.children[i];
        }
        return node;
    }

    function pathOf(el) {
        const segs = [];
        let n = el;
        while (n && n.parentElement && n.parentElement.id !== ROOT_ID) {
            const parent = n.parentElement;
            segs.push(Array.prototype.indexOf.call(parent.children, n).toString());
            n = parent;
        }
        // Top-level child has index 0 under the root wrapper.
        segs.reverse();
        // The root is the single child of #kohui-root; strip its leading "0".
        return segs.join(".");
    }

    function setPathDataAttrs(el, path) {
        if (!el) return;
        el.dataset.kohuiPath = path;
        for (let i = 0; i < el.children.length; i++) {
            setPathDataAttrs(el.children[i], path === "" ? i.toString() : `${path}.${i}`);
        }
    }

    function typeOf(el) {
        for (const c of el.classList) {
            if (c.startsWith("kohui-") && !c.startsWith("kohui-stack-")) {
                const name = c.slice("kohui-".length);
                return name[0].toUpperCase() + name.slice(1);
            }
        }
        return "";
    }

    function readProps(el) {
        // Minimal reflection of DOM state back into the shadow props. The
        // subset we care about round-trips correctly for diff-apply.
        const props = {};
        const type = typeOf(el);
        if (type === "Label" || type === "Button") props.text = el.textContent;
        if (type === "Button") props.enabled = !el.disabled;
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

    document.addEventListener("DOMContentLoaded", connect);
})();
