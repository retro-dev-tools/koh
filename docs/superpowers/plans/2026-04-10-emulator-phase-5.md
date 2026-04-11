# Koh Emulator — Phase 5 Implementation Plan (Optional / Future)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the optional Phase 5 items from the spec — a MAUI Blazor Hybrid desktop shell for `Koh.Emulator.App`, a published static-site "Koh Playground" for sharing ROMs, link-cable emulation between two instances (stretch), and `Koh.Lsp` integration for "where is PC right now in my source" highlighting during a debug session. **Time-travel debugging is out of scope for this plan** — it gets a separate future design.

**Architecture:** Phase 5 is explicitly optional. Each sub-section can be implemented independently and shipped separately. This plan is intentionally lighter than Phases 1–4 because the work is optional and the concrete value of each sub-item will be re-evaluated when Phases 1–4 are complete.

**Tech Stack:** MAUI (`net10.0-windows10.0.x` / `net10.0-maccatalyst` / `net10.0-android` target frameworks — Windows and macOS are the primary targets), otherwise unchanged.

**Prerequisites:** Phases 1, 2, 3, and 4 all complete with their exit criteria satisfied. Phase 5 builds on the fully-working emulator from Phase 4.

**Scope note:** Each item in this plan can be deferred indefinitely. Skipping Phase 5 entirely is a valid choice. If only some items are implemented, the remaining items stay as open future work.

---

## Phase 5-A: MAUI Blazor Hybrid desktop shell

Wraps `Koh.Emulator.App`'s Razor components in a native desktop app via MAUI Blazor Hybrid. The MAUI shell runs the same C# emulator logic and Razor UI inside the host OS's system WebView (WebView2 on Windows, WKWebView on macOS) — no Chromium runtime is bundled.

### Task 5.A.1: MAUI Blazor Hybrid project scaffold

**Files:**
- Create: `src/Koh.Emulator.Maui/Koh.Emulator.Maui.csproj`
- Create: `src/Koh.Emulator.Maui/MauiProgram.cs`
- Create: `src/Koh.Emulator.Maui/App.xaml` + `.xaml.cs`
- Create: `src/Koh.Emulator.Maui/MainPage.xaml` + `.xaml.cs`
- Modify: `Koh.slnx`

- [ ] **Step 1: Create `Koh.Emulator.Maui.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFrameworks>net10.0-windows10.0.19041.0;net10.0-maccatalyst</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <UseMaui>true</UseMaui>
    <SingleProject>true</SingleProject>
    <EnableDefaultCssItems>false</EnableDefaultCssItems>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Koh.Emulator.Core\Koh.Emulator.Core.csproj" />
    <ProjectReference Include="..\Koh.Debugger\Koh.Debugger.csproj" />
    <ProjectReference Include="..\Koh.Emulator.App\Koh.Emulator.App.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create `MauiProgram.cs`**

```csharp
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.Logging;
using Koh.Emulator.App.Services;
using Koh.Emulator.App.Shell;

namespace Koh.Emulator.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddMauiBlazorWebView();

        // Reuse the same service registrations from Koh.Emulator.App/Program.cs
        builder.Services.AddSingleton<RuntimeModeDetector>();
        builder.Services.AddSingleton<FramePacer>();
        builder.Services.AddSingleton<EmulatorHost>();
        builder.Services.AddSingleton<FramebufferBridge>();
        builder.Services.AddSingleton<WebAudioBridge>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
```

- [ ] **Step 3: Create `App.xaml`, `MainPage.xaml` hosting the Blazor WebView**

`MainPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:local="clr-namespace:Koh.Emulator.Maui"
             xmlns:appshell="clr-namespace:Koh.Emulator.App.Shell;assembly=Koh.Emulator.App"
             x:Class="Koh.Emulator.Maui.MainPage"
             Title="Koh Emulator">
    <BlazorWebView HostPage="wwwroot/index.html">
        <BlazorWebView.RootComponents>
            <RootComponent Selector="#app" ComponentType="{x:Type appshell:StandaloneShell}" />
        </BlazorWebView.RootComponents>
    </BlazorWebView>
</ContentPage>
```

- [ ] **Step 4: Copy the Blazor wwwroot assets into the MAUI project**

```xml
<ItemGroup>
  <MauiAsset Include="..\Koh.Emulator.App\wwwroot\**\*">
    <Link>wwwroot\%(RecursiveDir)%(Filename)%(Extension)</Link>
  </MauiAsset>
</ItemGroup>
```

- [ ] **Step 5: Register in `Koh.slnx`**

- [ ] **Step 6: Build (at least on the primary platform)**

```bash
dotnet build src/Koh.Emulator.Maui -f net10.0-windows10.0.19041.0
```

- [ ] **Step 7: Commit**

```bash
git add src/Koh.Emulator.Maui/ Koh.slnx
git commit -m "feat(emulator-maui): scaffold MAUI Blazor Hybrid desktop shell"
```

---

### Task 5.A.2: Platform integration — file access, audio, window

**Files:**
- Create: `src/Koh.Emulator.Maui/Platforms/Windows/FilePickerAdapter.cs`
- Create: `src/Koh.Emulator.Maui/Platforms/MacCatalyst/FilePickerAdapter.cs`
- Modify: `src/Koh.Emulator.App/StandaloneMode/RomFilePicker.razor`

Per spec §Phase 5: "significant reuse, modest per-platform code." File access, audio backend, window lifecycle, and packaging all need platform-specific attention.

- [ ] **Step 1: Abstract the ROM loader into an interface**

```csharp
namespace Koh.Emulator.App.Services;

public interface IFileSystemAccess
{
    Task<byte[]?> PickRomAsync();
    Task<byte[]?> PickSaveStateAsync();
    Task SaveSaveStateAsync(string defaultName, byte[] data);
}
```

In the Blazor WASM build, this is implemented via the HTML `<input type=file>` flow. In the MAUI build, this uses `FilePicker.Default.PickAsync`.

- [ ] **Step 2: MAUI implementation**

```csharp
public sealed class MauiFileSystemAccess : IFileSystemAccess
{
    public async Task<byte[]?> PickRomAsync()
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = new[] { ".gb", ".gbc" },
                [DevicePlatform.MacCatalyst] = new[] { "com.nintendo.gameboy.rom" },
            }),
        });
        if (result is null) return null;
        using var stream = await result.OpenReadAsync();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    public Task<byte[]?> PickSaveStateAsync() { /* similar */ throw new NotImplementedException(); }
    public Task SaveSaveStateAsync(string defaultName, byte[] data) { /* similar */ throw new NotImplementedException(); }
}
```

Register in `MauiProgram.CreateMauiApp`:

```csharp
builder.Services.AddSingleton<IFileSystemAccess, MauiFileSystemAccess>();
```

For the WASM build, add a separate `BrowserFileSystemAccess` implementation.

- [ ] **Step 3: Audio backend**

The Blazor WASM `WebAudioBridge` works inside the MAUI WebView too (MAUI Blazor uses the same WebView JS interop). No platform-specific audio code needed beyond verifying that WebAudio works in WebView2 and WKWebView.

- [ ] **Step 4: Window title updates when a ROM loads**

```csharp
// In MainPage.xaml.cs, observe EmulatorHost.StateChanged and update Title.
```

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Emulator.Maui/Platforms/ src/Koh.Emulator.App/Services/IFileSystemAccess.cs src/Koh.Emulator.App/Services/BrowserFileSystemAccess.cs src/Koh.Emulator.Maui/MauiProgram.cs
git commit -m "feat(emulator-maui): add platform file picker + window integration"
```

---

### Task 5.A.3: MAUI packaging

**Files:**
- Create: `src/Koh.Emulator.Maui/Platforms/Windows/Package.appxmanifest`
- Create: `src/Koh.Emulator.Maui/Platforms/MacCatalyst/Info.plist`
- Modify: `build.cake`

- [ ] **Step 1: Create the Windows MSIX manifest**

Standard MAUI packaging manifest with app identity, display name, publisher. MAUI templates provide a starting point.

- [ ] **Step 2: Create macOS `Info.plist`**

- [ ] **Step 3: Add a Cake task for publishing the MAUI app**

```csharp
Task("publish-maui-windows")
    .Does(() =>
{
    DotNet("publish src/Koh.Emulator.Maui -f net10.0-windows10.0.19041.0 -c Release");
});

Task("publish-maui-macos")
    .Does(() =>
{
    DotNet("publish src/Koh.Emulator.Maui -f net10.0-maccatalyst -c Release");
});
```

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Maui/Platforms/Windows/Package.appxmanifest src/Koh.Emulator.Maui/Platforms/MacCatalyst/Info.plist build.cake
git commit -m "chore(maui): add packaging manifests and Cake publish tasks"
```

---

## Phase 5-B: Koh Playground (published static site)

The same Blazor WASM AOT build that ships in the VS Code extension also works as a standalone static site. Publishing it to a public URL gives users a "Koh Playground" where they can drop ROMs and play without installing anything.

### Task 5.B.1: Static-site publish pipeline

**Files:**
- Create: `.github/workflows/playground-deploy.yml`
- Create: `docs/playground.md`

- [ ] **Step 1: Create the deployment workflow**

```yaml
name: Deploy Koh Playground

on:
  push:
    branches: [master]
    paths:
      - 'src/Koh.Emulator.App/**'
      - 'src/Koh.Emulator.Core/**'
      - 'src/Koh.Debugger/**'
  workflow_dispatch:

permissions:
  contents: read
  pages: write
  id-token: write

jobs:
  deploy:
    environment:
      name: github-pages
      url: ${{ steps.deployment.outputs.page_url }}
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Install wasm-tools
        run: dotnet workload install wasm-tools
      - name: Publish Blazor WASM
        run: dotnet publish src/Koh.Emulator.App -c Release -p:RunAOTCompilation=true -o ./playground-publish
      - name: Fix base href for GitHub Pages
        run: |
          sed -i 's|<base href="/" />|<base href="/koh/" />|g' ./playground-publish/wwwroot/index.html
      - uses: actions/upload-pages-artifact@v3
        with:
          path: ./playground-publish/wwwroot
      - id: deployment
        uses: actions/deploy-pages@v4
```

- [ ] **Step 2: Create `docs/playground.md`**

```markdown
# Koh Playground

The Koh Playground is a static-site deployment of `Koh.Emulator.App` in
standalone mode. Users can drop a Game Boy ROM (`.gb` or `.gbc`) into the
browser and play it directly, without installing anything.

URL: https://<github-user>.github.io/koh/

## How it's deployed

The `.github/workflows/playground-deploy.yml` GitHub Actions workflow
publishes the Blazor WASM app to GitHub Pages on every push to master that
touches the emulator or its dependencies. Deployment takes ~5 minutes
due to AOT compilation.

## Caveats

- First load is slow (~2-5 seconds to download the Blazor runtime).
- Save states persist via browser localStorage; they're per-browser.
- No debugging — the playground is standalone mode only.
- ROMs are loaded client-side and never uploaded anywhere.
```

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/playground-deploy.yml docs/playground.md
git commit -m "ci: add Koh Playground deployment workflow"
```

---

## Phase 5-C: LSP integration for "where is PC" highlighting

When a debug session is active, the Koh LSP gains a new decoration that highlights the current PC's source line in any open editor. This ties `Koh.Debugger` to `Koh.Lsp` via VS Code extension messaging.

### Task 5.C.1: Debug session → LSP bridge in the extension

**Files:**
- Create: `editors/vscode/src/debug/LspHighlightBridge.ts`
- Modify: `editors/vscode/src/core/KohExtension.ts`

- [ ] **Step 1: Create `LspHighlightBridge.ts`**

```typescript
import * as vscode from 'vscode';
import { LanguageClient } from 'vscode-languageclient/node';
import { WebviewToExtensionMessage } from '../webview/messages';

export class LspHighlightBridge implements vscode.Disposable {
    private decorationType: vscode.TextEditorDecorationType | undefined;
    private activeSession: vscode.DebugSession | undefined;

    constructor(private readonly client: LanguageClient) {}

    onDebugSessionStart(session: vscode.DebugSession): void {
        this.activeSession = session;
        this.decorationType = vscode.window.createTextEditorDecorationType({
            backgroundColor: new vscode.ThemeColor('editor.stackFrameHighlightBackground'),
            isWholeLine: true,
        });
    }

    onDebugSessionStop(): void {
        this.activeSession = undefined;
        this.decorationType?.dispose();
        this.decorationType = undefined;
    }

    onCpuStateUpdate(message: { pc: number; bank: number; file: string; line: number }): void {
        if (!this.decorationType) return;
        const editor = vscode.window.visibleTextEditors.find(e => e.document.fileName.endsWith(message.file));
        if (!editor) return;
        const range = new vscode.Range(message.line - 1, 0, message.line - 1, 0);
        editor.setDecorations(this.decorationType, [range]);
    }

    dispose(): void {
        this.decorationType?.dispose();
    }
}
```

- [ ] **Step 2: Wire into `EmulatorPanelHost`**

When the webview sends a `koh.cpuState` event, forward the `pc/bank/file/line` fields to `LspHighlightBridge.onCpuStateUpdate`.

This requires the debugger to include source location in its `koh.cpuState` custom events. Update `Koh.Debugger.DebugSession` to resolve the current PC to a source location via `SourceMap` and include it in the CPU-state event payload.

- [ ] **Step 3: Hook session lifecycle in `KohExtension`**

```typescript
vscode.debug.onDidStartDebugSession(session => {
    if (session.type === 'koh') this.lspHighlight.onDebugSessionStart(session);
});
vscode.debug.onDidTerminateDebugSession(session => {
    if (session.type === 'koh') this.lspHighlight.onDebugSessionStop();
});
```

- [ ] **Step 4: Test manually**

F5 a Koh project, set a breakpoint, step through. Verify the current PC line is highlighted in the editor.

- [ ] **Step 5: Commit**

```bash
git add editors/vscode/src/debug/LspHighlightBridge.ts editors/vscode/src/core/KohExtension.ts src/Koh.Debugger/DebugSession.cs
git commit -m "feat(debugger): add current-PC source highlighting via LSP decoration"
```

---

## Phase 5-D: Link-cable emulation (stretch)

Link-cable multiplayer requires emulating the Serial Data Transfer between two instances. This is a significant feature in its own right and the spec explicitly marks it as "stretch" for Phase 5. It may be deferred indefinitely.

### Task 5.D.1: Serial protocol abstraction

**Files:**
- Create: `src/Koh.Emulator.Core/Serial/ISerialLink.cs`
- Modify: `src/Koh.Emulator.Core/Serial/Serial.cs`

- [ ] **Step 1: Define `ISerialLink`**

```csharp
namespace Koh.Emulator.Core.Serial;

public interface ISerialLink
{
    byte ExchangeByte(byte sent);   // send one byte, receive one byte (8-clock sync)
}
```

- [ ] **Step 2: Update `Serial` to use an optional link**

When `ISerialLink` is set, transfers are routed through it instead of the Phase 1/3 local buffer.

- [ ] **Step 3: Test with a loopback `ISerialLink` (both ends are the same instance)**

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Serial/
git commit -m "feat(serial): add ISerialLink abstraction for link-cable emulation"
```

---

### Task 5.D.2: WebRTC-based link between two browser instances

**Files:**
- Create: `src/Koh.Emulator.App/Services/WebRtcLink.cs`
- Create: `src/Koh.Emulator.App/wwwroot/js/webrtc-link.js`

- [ ] **Step 1: Create the JS bridge**

The WebRTC connection uses a data channel. One instance creates an offer, the other accepts. Signaling happens via manual paste-swap for the prototype; a signaling server could be added later.

```javascript
window.kohWebRtcLink = (function () {
    let pc = null;
    let channel = null;
    let blazorRef = null;

    return {
        register: function (dotNetRef) { blazorRef = dotNetRef; },

        createOffer: async function () {
            pc = new RTCPeerConnection();
            channel = pc.createDataChannel('koh-serial', { ordered: true });
            channel.onmessage = function (e) {
                blazorRef?.invokeMethodAsync('OnByteReceived', new Uint8Array(e.data)[0]);
            };
            const offer = await pc.createOffer();
            await pc.setLocalDescription(offer);
            return JSON.stringify(offer);
        },

        acceptOffer: async function (offerJson) { /* ... */ },
        sendByte: function (byte) { if (channel?.readyState === 'open') channel.send(new Uint8Array([byte])); },
    };
})();
```

- [ ] **Step 2: Create `WebRtcLink.cs`** — implements `ISerialLink` and bridges to the JS via interop.

This task is prototype-level. A production implementation would add signaling, turn servers, and error recovery.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.App/Services/WebRtcLink.cs src/Koh.Emulator.App/wwwroot/js/webrtc-link.js
git commit -m "feat(serial): add WebRTC link-cable prototype (stretch)"
```

---

## Phase 5 exit checklist

Phase 5 items are independent. Each checkmark reflects a completed sub-goal; not all are required to consider Phase 5 "done" because the whole phase is optional.

- [ ] MAUI desktop shell builds on Windows and runs the standalone emulator
- [ ] MAUI desktop shell runs on macOS (if macOS target is in scope)
- [ ] MAUI platform file picker works for loading ROMs and save states
- [ ] Koh Playground deploys to GitHub Pages and is publicly accessible
- [ ] LSP "where is PC" highlighting works during debug sessions
- [ ] (Stretch) Link-cable prototype exchanges bytes between two instances

---

## Self-review notes

**Spec coverage:**

- §3 non-goals: reverse execution correctly deferred out of this plan
- §Phase 5 design: MAUI Blazor Hybrid desktop shell (soft "significant reuse" language honored — platform adapters for file access and packaging), playground site, link cable, LSP integration
- §Phase 5 explicit note: "Time-travel / reverse execution — separate future design. Out of scope for this spec." ✓

**Known deferrals even inside Phase 5:**
- Signaling server for WebRTC link cable (prototype uses manual paste-swap)
- macOS code-signing and notarization for MAUI distribution
- Mobile (Android, iOS) targets for MAUI
- Accessibility audit of the webview UI

**Known risks:**
- MAUI on macOS has rougher edges than on Windows; expect platform-specific debugging
- GitHub Pages base-href rewriting is fragile; verify after each deployment
- WebRTC link cable is a meaningful project on its own and may deserve its own spec if pursued seriously

---

**Plan complete.** Phase 5 is optional — implementation is expected to happen incrementally as the core Phases 1–4 stabilize and specific user demand emerges.
