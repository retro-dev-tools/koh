# KohUI — Retro-themed MVU UI Framework Design

## Goals, in priority order

1. **NativeAOT** from day one. Publish a single self-contained executable; zero runtime reflection on hot paths; AOT analyzer clean across the whole graph.
2. **Single-file dependency surface.** `PublishSingleFile=true` + `EnableCompressionInSingleFile=true` produce one `.exe` per RID. KohUI's own assets (HTML shell, 98.css, patch-applier JS) live as embedded resources — never as loose files next to the binary. Third-party native libs (GLFW) get bundled as `RuntimeHostConfigurationOption` single-file extractions, not separate files the user can drop or break.
3. **Dev-time web preview.** Same binary boots into a browser-visible mode so coding agents (Playwright / WebDriver-BiDi / MCP) can drive the real application tree at DOM level without any mock runtime. Preview and production share the reconciler and view code; only the backend differs.
4. **Performance.** 60 fps on a 100-node tree on laptop integrated graphics. Zero allocations per frame in steady state on the GL path; the DOM path amortises the patch-list allocation to "one array of records per dispatched message".
5. **Windows-98 aesthetic** for the retro-dev-tool scene. Bevels, chiseled borders, Chicago font, menu bars, modeless child windows — shipped as a first-class theme, not an add-on.

The UI surface matters, but the first four items are what make KohUI worth building rather than reusing. If we ship a beautiful 98 theme on top of a 120 MB Avalonia binary that can't NativeAOT, we've missed the point.

## Non-goals

- Not a general-purpose UI framework. Opinionated about Windows-98 chrome, bevel-style panels, MS Sans Serif text, modeless child windows. Other aesthetics live as themes later, not v0.1.
- Not competitive with Avalonia/Uno on widget breadth. v0.1 ships ~15 widgets sufficient for retro tools.
- Cross-platform from day one: Windows, Linux, macOS. NativeAOT on all three. No platform-specific windowing code in the core.
- No vector graphics API, no animation engine, no accessibility layer in v0.1 (DOM backend inherits HTML a11y semantics for free).

## Architecture

```
  Model (record) ── update(Msg, Model) → Model ── view(Model) → Component tree
                                                                      │
                                                    ┌─────────────────┴─────────────────┐
                                                    ▼                                   ▼
                                      Reconciler (diff old vs new)        Patch list: Insert/Replace/Remove/UpdateProp
                                                                                      │
                                                  ┌───────────────────────────────────┴───────────────────────────────────┐
                                                  ▼                                                                       ▼
                                  GlBackend (native)                                                   DomBackend (web preview, v0.1 focus)
                                  ────────────────────────────                                           ────────────────────────────────
                                  GLFW (window + input, all OSes)                                        Kestrel Minimal API + WebSocket
                                  Silk.NET.OpenGL 3.3 core + quad batcher                                98.css static page
                                  Spec-driven layouter                                                   HTML patch applier
                                  Hand-drawn Win98 bevels                                                Playwright-drivable at DOM level
```

**Elm-style MVU:**
- `Model` is an immutable record.
- `update(msg, model) → newModel` — pure.
- `view(model) → Component` — pure; returns a component tree (also records).
- `KohRunner` owns the event loop: processes messages, calls update + view, diffs, emits patches.

**Views are typed structs (Xilem-style), not boxed records:**

View construction is zero-allocation — the compiler monomorphizes the tree. Following Xilem / Masonry's design thesis: transient views are cheap and generic, retained widgets hold local state (focus, hover, scroll position).

```csharp
// TMsg = the discriminated-union message type of the owning app.
// Views don't know about TModel; the view tree is pure rendering instructions.
public interface IView<TMsg>
{
    RenderNode Render();                        // produce patch-list payload
    // Rebuild(previous) will be added in Phase 1 for retained-state widgets.
}

// Leaf view — struct, fully inlinable.
public readonly struct Button<TMsg>(string Text, Func<TMsg>? OnClick = null) : IView<TMsg>
{
    public readonly string Text = Text;
    public readonly Func<TMsg>? OnClick = OnClick;

    public RenderNode Render() => new("button", new { text = Text, onClick = OnClick is not null });
}

// Container view — generic over child view types. Compiler monomorphizes the chain.
public readonly struct Window<TMsg, TChild>(string Title, TChild Child) : IView<TMsg>
    where TChild : IView<TMsg>
{
    public readonly string Title = Title;
    public readonly TChild Child = Child;

    public RenderNode Render() => new("window", new { title = Title }, [Child.Render()]);
}

// Variadic children use `Tuple2<A,B>`, `Tuple3<A,B,C>` or a plain `ForEach<T>`
// for dynamic lists — the only path that boxes.
```

Dispatcher: `Func<TMsg>` returning a message is the idiomatic shape. The runner invokes the delegate on DOM events and routes the result through `update`. Closures over app state are not needed (views are pure), so the per-render allocation cost of `Func<TMsg>` is bounded and measurable — optimize to static delegates or message refs if profiling demands it.

## Drivability — two channels for agents, screen readers, and tests

Any KohUI app exposes its UI tree through **two** independent interfaces; both are always on in the respective backend:

| Backend | Drivable via | Agent workflow |
|---|---|---|
| DomBackend | DOM (real `<div>`s with stable `data-kohui-id`) | Playwright / any WebDriver-BiDi client |
| GlBackend | **AccessKit** — bridges to UIA (Win), AT-SPI (Linux), NSAccessibility (macOS) | Any OS a11y automation tool; screen readers for free; MCP server on top is trivial |

The reconciler emits the same node tree to both. DomBackend serialises to HTML, GlBackend serialises to `accesskit::Node` updates. **A coding agent gets a structured, semantically-typed view of the UI whether the app is running in native or preview mode.**

This is the piece that's genuinely novel — no other .NET UI framework ships proper a11y, let alone unified a11y + web-preview automation.

## Dev preview (Playwright story)

One binary, two modes:

- `kohui-app` → both backends: GL window opens + Kestrel on localhost. Dev workflow.
- `kohui-app --dev` → DomBackend only. Kestrel on localhost prints URL. Playwright attaches. Headless CI-friendly.
- `kohui-app --headless` → DomBackend only, no browser launch. Same as `--dev` but for automation.

Production-published apps embed both backends and default to native. A `--preview` flag exposes the preview port when the user wants to debug layout in DevTools.

## WebSocket patch protocol

Shape of each message (JSON):

```jsonc
// Initial render
{"op": "replace", "path": "/", "node": { "type": "Window", "id": "w1", "props": {...}, "children": [...] }}

// Single-prop update (e.g. button label changed)
{"op": "set", "path": "/w1/c0/b2", "key": "Text", "value": "Count: 3"}

// Subtree change
{"op": "replace", "path": "/w1/c0", "node": {...}}

// Node removed
{"op": "remove", "path": "/w1/c0/b3"}

// Event dispatched from DOM → server
// (separate up-channel, same WebSocket)
{"op": "event", "path": "/w1/c0/b2", "event": "click"}
```

Paths are hierarchical component IDs (stable across re-renders via the `Key` field + positional index fallback). The DomBackend assigns IDs during reconciliation.

Server-side: Kestrel Minimal API, one WebSocket per connected preview. Events arrive → converted to `Msg` → fed to runner.

Client-side JS (small, ~500 LOC, no framework):
- Receive JSON patch → apply to DOM (create/remove/update attributes).
- Serialize component-type names to HTML element + class (`Window` → `<div class="window">`).
- Attach event listeners that send `{op: "event", path, event}` back.

## Stack (all MIT/BSD, all AOT-safe)

| Role | Choice | Notes |
|---|---|---|
| MVU runtime | hand-rolled, ~300 LOC | No external reactive framework needed; Model→Msg→Model is trivial. |
| Reconciler | hand-rolled, ~500 LOC | Key-based diff with positional fallback. |
| Layout (DOM) | browser CSS | 98.css (+ generated per-widget rules) handles preview mode. |
| Layout (GL) | hand-rolled spec-driven Layouter | Two-pass measure/arrange, metrics sourced from the same `WidgetSpec` table the CSS generator reads. |
| 2D rendering (GL) | Silk.NET.OpenGL 3.3 core | Single-shader quad batcher + 6×8 bitmap-font atlas. AOT-clean (`LibraryImport` source-gen). |
| Windowing + input (GL) | Silk.NET.GLFW | GLFW native (~230 KB) per RID covers HWND / X11 / NSWindow creation, keyboard, mouse, cursor. Blittable P/Invoke → AOT-safe. |
| DomBackend server | ASP.NET Core Minimal API + WebSocket | Already AOT-proven; cross-platform by construction. |
| Visual spec | [98.css](https://jdan.github.io/98.css/) | Vendored into DomBackend wwwroot. |
| A11y / agent surface | [AccessKit](https://accesskit.dev/) | Rust lib with C bindings; MIT/Apache. Used by egui / Slint / Makepad / Zed. Bound via `LibraryImport` + `StructLayout.Sequential`. |
| Tests | TUnit | Same as rest of Koh repo. |

**Platform matrix for the GlBackend publish target:**

| RID | Native deps bundled | Notes |
|---|---|---|
| `win-x64`, `win-arm64` | `glfw3.dll` | Primary target; ~4.7 MB total (AOT exe + glfw3). |
| `linux-x64`, `linux-arm64` | `libglfw.so.3` | Requires GL + X11/Wayland libraries on host (standard on any desktop distro). |
| `osx-x64`, `osx-arm64` | `libglfw.3.dylib` | Needs code-signing for distribution, same as any other .NET desktop app. |

## Project layout (monorepo)

```
src/
  KohUI/                    # Core: Component records, Msg, Model, reconciler, IBackend
    Component.cs
    Msg.cs
    Runner.cs               # MVU loop
    Reconciler.cs           # tree diff → patch list
    Patch.cs                # union type for patch ops
    Widgets/                # Window, Button, Label, etc. (records)

  KohUI.Backends.Dom/       # Web preview + Playwright backend
    KohUI.Backends.Dom.csproj
    DomBackend.cs           # Implements IBackend
    Program.cs              # Kestrel host entry for standalone dev mode
    PatchSerializer.cs      # Component → HTML + props
    wwwroot/
      index.html
      98.css                # Vendored from jdan/98.css
      kohui-client.js       # WebSocket + patch applier

  KohUI.Backends.Gl/        # Native backend (GLFW + OpenGL 3.3 core)
    GlBackend.cs
    GlContext.cs
    QuadBatch.cs            # One-shader quad batcher
    BitmapFont.cs           # Embedded 6×8 ASCII atlas
    Layout.cs               # Two-pass spec-driven layouter
    Painter.cs              # Walks LayoutNode → quads + glyphs

samples/
  KohUI.Demo/               # Gallery + counter demo
    Program.cs
    Demo.cs                 # Sample MVU app

tests/
  KohUI.Tests/              # Reconciler diff tests, patch protocol tests
```

## v0.1 widget inventory (15 components)

- Chrome: `Window`, `TitleBar` (auto-generated by Window), `MenuBar`, `MenuItem`, `StatusBar`, `Toolbar`
- Layout: `Panel` (bevel in/out/chiseled), `Splitter`, `TabControl`
- Input: `Button`, `TextBox`, `CheckBox`, `RadioButton`
- Data: `ListBox`, `TreeView`

Deferred to v0.2+: `ComboBox`, `ProgressBar`, `Dialog`, `Scrollbar` as first-class (HTML gives it for free, GL backend needs it), `Slider`, `GroupBox`, `NumericUpDown`.

## Timeline

| Phase | Scope | Estimate |
|---|---|---|
| 0 | MVU core + DomBackend + counter demo; Playwright smoke test | 2-3 days |
| 1 | Window chrome (title bar, drag, z-order), MenuBar, Panel, first real sample app | 1 week |
| 2 | Remaining v0.1 widgets + TreeView/ListView hooked to reactive collections | 1 week |
| 3 | GlBackend: GLFW window/input + hand-rolled Layouter + bevel paint library + parity with DomBackend; publish-size check on all six RIDs | 2 weeks |
| 4 | Port emulator UI from Blazor to KohUI as the first real consumer | 1 week |

## Open questions

- Event handlers in component records: `Func<Msg>` is simplest but captures closures → allocation per render. Alternative: address-style refs (`"OnClickMsg: #increment"`) and a `Dispatch(string)` table. TBD after Phase 0.
- Theming hooks: v0.1 is 98-only. Leave `ITheme` interface stubbed but don't implement XP/7/custom until someone asks.
- Animation: out of scope for v0.1. Win98 doesn't animate.
