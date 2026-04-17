# Koh Playground

The Koh Playground is a static-site deployment of `Koh.Emulator.App` in
standalone mode. Users drop a Game Boy ROM (`.gb` or `.gbc`) into the browser
and play it directly without installing anything.

## How it is deployed

`.github/workflows/playground-deploy.yml` runs on every push to `master` that
touches the emulator or its dependencies. It publishes the Blazor WebAssembly
app with AOT compilation, patches `<base href>` for the GitHub Pages subpath,
writes a `.nojekyll` file so the `_framework/` folder is served, and uploads
the artifact to GitHub Pages. First-time setup requires enabling Pages in the
repo's **Settings → Pages** with source set to **GitHub Actions**.

Deployment takes roughly 5 minutes; the AOT compile dominates.

## Caveats

- First page load is slow (~2–5 seconds to download the runtime) because the
  Blazor WebAssembly runtime is large. Subsequent loads hit the browser cache.
- Save states persist via browser storage (per-browser, not per-ROM).
- The playground is standalone-mode only. Debugging requires the VS Code
  extension since DAP integration needs the extension's bridge.
- ROMs are loaded and processed client-side only; nothing is uploaded.
