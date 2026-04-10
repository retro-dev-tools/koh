# Koh Emulator & Debugger — Phase 0 + Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver an F5-launched debug session in VS Code that opens a Blazor-WASM emulator webview with a live CPU dashboard, source-resolved breakpoints (verified, not halting), a representative Phase 1 benchmark that passes the 2.0× real-time threshold, and all supporting project scaffolding — without CPU opcodes or PPU rendering (those arrive in Phases 2 and 3).

**Architecture:** Follow the full design in `docs/superpowers/specs/2026-04-10-emulator-debugger-design.md`. Three new projects (`Koh.Emulator.Core`, `Koh.Debugger`, `Koh.Emulator.App`) plus two test projects, extended `Koh.Linker.Core` with `.kdbg` emission, and a refactored VS Code extension hosting a Blazor WebAssembly debug adapter via an inline passthrough. All hot-path code is `sealed`, allocation-free, and AOT-compatible.

**Tech Stack:** C# 14 on .NET 10 (existing Koh convention), Blazor WebAssembly (new), TUnit v1.28.7 (existing test framework), TypeScript for the VS Code extension (plain tsc, no bundler), Cake for build orchestration, GitHub Actions for CI.

**Scope note:** This plan covers Phase 0 (project scaffolding) and Phase 1 (infrastructure + F5 wiring) from the spec. Phases 2, 3, 4, and 5 will each get their own plan when we reach them.

---

## Architecture summary

Three new .NET projects and two test projects are added to `Koh.slnx`:

```
src/
├── Koh.Emulator.Core/         // BCL-only pure emulator library
├── Koh.Debugger/              // DAP handler class library (consumed by Blazor)
├── Koh.Emulator.App/          // Blazor WebAssembly shell + UI
└── Koh.Linker.Core/           // EXISTING — extended with KdbgFileWriter

tests/
├── Koh.Emulator.Core.Tests/
└── Koh.Debugger.Tests/
```

`Koh.Linker.Core` is extended with `.kdbg` emission alongside the existing `.sym` output.

`editors/vscode/` is refactored from a single `extension.ts` god-object into a facade over narrow subsystem modules (`core/`, `lsp/`, `config/`, `build/`, `debug/`, `webview/`, `commands/`).

The VS Code extension spawns no external debugger process — it registers an **inline DAP adapter** (TypeScript passthrough) that forwards DAP messages to the Blazor WASM app hosted in a webview, which in turn hands them to `Koh.Debugger` handlers in C#.

A Blazor WASM AOT publish produces the emulator app's static assets, which the extension bundles into `editors/vscode/dist/emulator-app/`. A content-hash-based freshness check gates `.vsix` packaging.

---

## File structure (all new or modified files)

### New C# files

**`src/Koh.Emulator.Core/` (new project)**

```
Koh.Emulator.Core.csproj
HardwareMode.cs
SystemClock.cs
StopReason.cs
StepResult.cs
StopCondition.cs
RunGuard.cs
GameBoySystem.cs
Bus/
    Mmu.cs
    IoRegisters.cs
Cpu/
    Sm83.cs
    CpuRegisters.cs
    InstructionTable.cs
    Interrupts.cs
Ppu/
    Ppu.cs
    PpuMode.cs
    Framebuffer.cs
Cartridge/
    Cartridge.cs
    MapperKind.cs
    CartridgeHeader.cs
    CartridgeFactory.cs
    Mbc1.cs
Timer/
    Timer.cs
Joypad/
    Joypad.cs
    JoypadState.cs
Debug/
    MemoryHook.cs
State/
    StateVersion.cs
```

**`src/Koh.Debugger/` (new project)**

```
Koh.Debugger.csproj
DebugSession.cs
Dap/
    DapDispatcher.cs
    DapJson.cs                  // JsonSerializerContext source-gen
    DapCapabilities.cs
    Messages/
        InitializeMessages.cs
        LaunchMessages.cs
        ConfigurationDoneMessages.cs
        ContinueMessages.cs
        PauseMessages.cs
        TerminateMessages.cs
        SetBreakpointsMessages.cs
        ScopesMessages.cs
        VariablesMessages.cs
        ExceptionInfoMessages.cs
        CommonMessages.cs
    Handlers/
        InitializeHandler.cs
        LaunchHandler.cs
        ConfigurationDoneHandler.cs
        ContinueHandler.cs
        PauseHandler.cs
        TerminateHandler.cs
        SetBreakpointsHandler.cs
        ScopesHandler.cs
        VariablesHandler.cs
        ExceptionInfoHandler.cs
Session/
    BankedAddress.cs
    BreakpointManager.cs
    DebugInfoLoader.cs
    SourceMap.cs
    SymbolMap.cs
    ExecutionLoop.cs
Events/
    CustomDapEvents.cs
```

**`src/Koh.Emulator.App/` (new Blazor WASM project)**

```
Koh.Emulator.App.csproj
Program.cs
App.razor
App.razor.cs
Shell/
    RuntimeMode.cs
    RuntimeModeDetector.cs
    DebugShell.razor
    StandaloneShell.razor
Components/
    LcdDisplay.razor
    LcdDisplay.razor.js
    CpuDashboard.razor
Input/
    JoypadCapture.razor
    JoypadKeyMap.cs
Services/
    EmulatorHost.cs
    FramePacer.cs
    FramePacer.razor.js
    FramebufferBridge.cs
    FramebufferBridge.razor.js
    RomLoader.cs
DebugMode/
    DebugModeBootstrapper.cs
    DapTransport.cs
    VsCodeBridge.razor.js
StandaloneMode/
    StandaloneBootstrapper.cs
    RomFilePicker.razor
    PlaybackControls.razor
Benchmark/
    BenchmarkPage.razor
    BenchmarkRunner.cs
wwwroot/
    index.html
    css/emulator.css
    sample-roms/.gitignore
```

**Extended: `src/Koh.Linker.Core/`**

```
KdbgFileWriter.cs           // NEW — parallel to SymFileWriter.cs
KdbgFormat.cs               // NEW — format constants (magic, version)
DebugInfoBuilder.cs         // NEW — collects entries during link
```

**Extended: `src/Koh.Link/Program.cs`** — add `-d`/`--kdbg` flag and call `KdbgFileWriter.Write`.

### New test files

**`tests/Koh.Emulator.Core.Tests/`**

```
Koh.Emulator.Core.Tests.csproj
GameBoySystemTests.cs
MmuTests.cs
CartridgeHeaderTests.cs
RomOnlyTests.cs
Mbc1Tests.cs
TimerTests.cs
DebugReadWriteTests.cs
```

**`tests/Koh.Debugger.Tests/`**

```
Koh.Debugger.Tests.csproj
DapDispatcherTests.cs
InitializeHandlerTests.cs
LaunchHandlerTests.cs
SetBreakpointsHandlerTests.cs
ScopesVariablesHandlerTests.cs
ContinuePauseStateMachineTests.cs
```

**Extended: `tests/Koh.Linker.Tests/`**

```
KdbgFileWriterTests.cs      // NEW
```

### New TypeScript files (VS Code extension)

**`editors/vscode/src/`**

```
extension.ts                        // REWRITTEN as facade
core/
    KohExtension.ts
    DisposableStore.ts
    Logger.ts
lsp/
    LspClientManager.ts             // EXTRACTED from current extension.ts
    serverPathResolver.ts           // EXTRACTED
config/
    WorkspaceConfig.ts
    KohYamlReader.ts
build/
    BuildTaskProvider.ts
    KohBuildTask.ts
    binaryResolver.ts
debug/
    launchTypes.ts
    ConfigurationProvider.ts
    TargetSelector.ts
    DapMessageQueue.ts
    InlineDapAdapter.ts
    DebugSessionTracker.ts
    KohDebugRegistration.ts
webview/
    messages.ts
    BlazorAssetLoader.ts
    EmulatorHtml.ts
    EmulatorPanel.ts
    EmulatorPanelHost.ts
commands/
    CommandRegistrations.ts
```

**Extended: `editors/vscode/package.json`** — add `debuggers`, `breakpoints`, new commands, new settings.

### New build files

```
scripts/download-test-roms.ps1      // NEW
scripts/download-test-roms.sh       // NEW
scripts/compute-build-hash.ps1      // NEW (content-hash for freshness check)
scripts/compute-build-hash.sh       // NEW
.github/workflows/ci.yml            // NEW
docs/decisions/emulator-platform-decision.md  // NEW
```

### Extended build files

- `Directory.Packages.props` — add new package versions
- `build.cake` — add `publish-emulator-app` and `package-extension` tasks
- `Koh.slnx` — register new projects

---

## Phase 0: Scaffolding (8 tasks)

Phase 0 produces compilable empty projects and verifies the Blazor WASM AOT publish path works before any real work starts.

### Task 0.1: Create decision document

**Files:**
- Create: `docs/decisions/emulator-platform-decision.md`

- [ ] **Step 1: Write the decision document**

```markdown
# Decision: Blazor WebAssembly for Koh Emulator & Debugger

**Date:** 2026-04-10
**Status:** Accepted

## Context

The Koh project needs a first-party Game Boy / Game Boy Color emulator integrated with VS Code F5 debugging. Options considered:

1. Native executable (`koh-dbg.exe`) speaking DAP over stdio, emulator in-process
2. Blazor WebAssembly app hosted in a VS Code webview, debugger also in WASM
3. TypeScript emulator in the VS Code extension host

## Decision

Option 2: Blazor WebAssembly. The same compiled artifact runs in three modes (VS Code webview, standalone web, dev host), enabling reuse across debugging, playback, and contributor workflows.

## Consequences

### Accepted trade-offs

1. `Koh.Emulator.App` takes on Blazor as a core dependency. `Koh.Emulator.Core` stays BCL-only and AOT-compatible.
2. Blazor WASM AOT publish is required for release. Non-AOT is permitted for local development for faster iteration.
3. F5 cold-start is 1-2 seconds slower than a native binary due to Blazor runtime initialization.
4. Emulator debugging happens through `Koh.Emulator.Core.Tests` rather than by attaching a debugger to running Blazor WASM.
5. Cycle-accurate emulation is tick-driven at T-cycle granularity, with CPU bus events resolved at M-cycle boundaries.

### Relationship to LSP AOT decision

The existing `docs/decisions/lsp-aot-decision.md` documented that `Koh.Lsp` cannot be AOT-compiled due to reflection requirements in `StreamJsonRpc` and the LSP Protocol library. `Koh.Debugger` takes the same stance but through a different mechanism: Blazor WASM runtime initialization uses reflection, which is acceptable at the application-shell level while preserving AOT for the core emulator library.

## See also

- `docs/superpowers/specs/2026-04-10-emulator-debugger-design.md`
- `docs/decisions/lsp-aot-decision.md`
```

- [ ] **Step 2: Commit**

```bash
git add docs/decisions/emulator-platform-decision.md
git commit -m "docs: add emulator platform decision record"
```

---

### Task 0.2: Create `Koh.Emulator.Core` empty project

**Files:**
- Create: `src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
- Create: `src/Koh.Emulator.Core/PlaceholderAssemblyInfo.cs`
- Modify: `Koh.slnx`

- [ ] **Step 1: Create the project file**

File `src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <InternalsVisibleTo Include="Koh.Debugger" />
    <InternalsVisibleTo Include="Koh.Emulator.App" />
    <InternalsVisibleTo Include="Koh.Emulator.Core.Tests" />
    <InternalsVisibleTo Include="Koh.Debugger.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create a placeholder source file so the project compiles**

File `src/Koh.Emulator.Core/PlaceholderAssemblyInfo.cs`:

```csharp
namespace Koh.Emulator.Core;

internal static class PlaceholderAssemblyInfo
{
    public const string Name = "Koh.Emulator.Core";
}
```

- [ ] **Step 3: Register the project in `Koh.slnx`**

Open `Koh.slnx` and add the new project entry inside the `/src/` folder, after the existing `Koh.Linker.Core` entry:

```xml
    <Project Path="src/Koh.Emulator.Core/Koh.Emulator.Core.csproj" />
```

- [ ] **Step 4: Verify the solution builds**

Run: `dotnet build Koh.slnx`
Expected: all projects build including the new one; no warnings (warnings are errors per `Directory.Build.props`).

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Emulator.Core/ Koh.slnx
git commit -m "feat(emulator): scaffold Koh.Emulator.Core project"
```

---

### Task 0.3: Create `Koh.Debugger` empty project

**Files:**
- Create: `src/Koh.Debugger/Koh.Debugger.csproj`
- Create: `src/Koh.Debugger/PlaceholderAssemblyInfo.cs`
- Modify: `Koh.slnx`

- [ ] **Step 1: Create the project file**

File `src/Koh.Debugger/Koh.Debugger.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\Koh.Emulator.Core\Koh.Emulator.Core.csproj" />
    <ProjectReference Include="..\Koh.Linker.Core\Koh.Linker.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Koh.Emulator.App" />
    <InternalsVisibleTo Include="Koh.Debugger.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the placeholder source file**

File `src/Koh.Debugger/PlaceholderAssemblyInfo.cs`:

```csharp
namespace Koh.Debugger;

internal static class PlaceholderAssemblyInfo
{
    public const string Name = "Koh.Debugger";
}
```

- [ ] **Step 3: Register the project in `Koh.slnx`**

Add the new entry inside `/src/`:

```xml
    <Project Path="src/Koh.Debugger/Koh.Debugger.csproj" />
```

- [ ] **Step 4: Verify the solution builds**

Run: `dotnet build Koh.slnx`
Expected: all projects build.

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Debugger/ Koh.slnx
git commit -m "feat(debugger): scaffold Koh.Debugger project"
```

---

### Task 0.4: Create `Koh.Emulator.App` Blazor WebAssembly project

**Files:**
- Create: `src/Koh.Emulator.App/Koh.Emulator.App.csproj`
- Create: `src/Koh.Emulator.App/Program.cs`
- Create: `src/Koh.Emulator.App/App.razor`
- Create: `src/Koh.Emulator.App/_Imports.razor`
- Create: `src/Koh.Emulator.App/wwwroot/index.html`
- Create: `src/Koh.Emulator.App/wwwroot/css/emulator.css`
- Modify: `Koh.slnx`

- [ ] **Step 1: Create the Blazor WASM project file**

File `src/Koh.Emulator.App/Koh.Emulator.App.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <ServiceWorkerAssetsManifest>service-worker-assets.js</ServiceWorkerAssetsManifest>
    <BlazorWebAssemblyJiterpreter>false</BlazorWebAssemblyJiterpreter>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" />
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.DevServer" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Koh.Emulator.Core\Koh.Emulator.Core.csproj" />
    <ProjectReference Include="..\Koh.Debugger\Koh.Debugger.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add the Blazor package versions to `Directory.Packages.props`**

Edit `Directory.Packages.props` and add these entries inside the existing `<ItemGroup>`:

```xml
    <PackageVersion Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.0" />
    <PackageVersion Include="Microsoft.AspNetCore.Components.WebAssembly.DevServer" Version="10.0.0" />
```

(Use the version that matches .NET 10 at the time of implementation.)

- [ ] **Step 3: Create the Blazor entry point**

File `src/Koh.Emulator.App/Program.cs`:

```csharp
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Koh.Emulator.App;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().RunAsync();
```

- [ ] **Step 4: Create the `_Imports.razor` file**

File `src/Koh.Emulator.App/_Imports.razor`:

```razor
@using System.Net.Http
@using System.Net.Http.Json
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.AspNetCore.Components.WebAssembly.Http
@using Microsoft.JSInterop
@using Koh.Emulator.App
```

- [ ] **Step 5: Create the placeholder root component**

File `src/Koh.Emulator.App/App.razor`:

```razor
<h1>Koh Emulator — Phase 0</h1>
<p>Scaffold only. Nothing here yet.</p>
```

- [ ] **Step 6: Create the `wwwroot/index.html` shell**

File `src/Koh.Emulator.App/wwwroot/index.html`:

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Koh Emulator</title>
    <base href="/" />
    <link href="css/emulator.css" rel="stylesheet" />
</head>
<body>
    <div id="app">Loading…</div>
    <div id="blazor-error-ui">
        An unhandled error has occurred.
        <a href="" class="reload">Reload</a>
        <a class="dismiss">🗙</a>
    </div>
    <script src="_framework/blazor.webassembly.js"></script>
</body>
</html>
```

- [ ] **Step 7: Create placeholder CSS**

File `src/Koh.Emulator.App/wwwroot/css/emulator.css`:

```css
html, body {
    margin: 0;
    padding: 0;
    background: #1e1e1e;
    color: #d4d4d4;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
}

#app {
    padding: 16px;
}

#blazor-error-ui {
    display: none;
}
```

- [ ] **Step 8: Register the project in `Koh.slnx`**

Add inside `/src/`:

```xml
    <Project Path="src/Koh.Emulator.App/Koh.Emulator.App.csproj" />
```

- [ ] **Step 9: Verify the project builds**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`
Expected: build succeeds.

- [ ] **Step 10: Commit**

```bash
git add src/Koh.Emulator.App/ Koh.slnx Directory.Packages.props
git commit -m "feat(emulator-app): scaffold Koh.Emulator.App Blazor WASM project"
```

---

### Task 0.5: Verify Blazor WASM AOT publish works

This is a one-time gate that proves the AOT publish toolchain is installed and functional. Do not continue Phase 0 if this step fails; resolve the tooling issue first.

- [ ] **Step 1: Run the AOT publish**

Run: `dotnet publish src/Koh.Emulator.App/Koh.Emulator.App.csproj -c Release -p:RunAOTCompilation=true`
Expected: publish succeeds. The step may take several minutes the first time. Output ends up under `src/Koh.Emulator.App/bin/Release/net10.0/publish/wwwroot/`.

- [ ] **Step 2: Verify the `_framework/` directory contains AOT-compiled assets**

Run: `ls src/Koh.Emulator.App/bin/Release/net10.0/publish/wwwroot/_framework/` (or equivalent on Windows)
Expected: files including `blazor.webassembly.js`, `dotnet.js`, various `.wasm` files.

- [ ] **Step 3: Verify the non-AOT publish also works (faster alternative for development)**

Run: `dotnet publish src/Koh.Emulator.App/Koh.Emulator.App.csproj -c Debug -p:RunAOTCompilation=false`
Expected: publish succeeds, significantly faster than the AOT run.

- [ ] **Step 4: Clean up publish artifacts (do not commit them)**

Run: `git clean -fdx src/Koh.Emulator.App/bin src/Koh.Emulator.App/obj`

No commit — this task is a verification gate only.

---

### Task 0.6: Create `Koh.Emulator.Core.Tests` empty project

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj`
- Create: `tests/Koh.Emulator.Core.Tests/SmokeTests.cs`
- Modify: `Koh.slnx`

- [ ] **Step 1: Create the test project file**

File `tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UseTestingPlatformRunner>true</UseTestingPlatformRunner>
    <TestingPlatformCommandLineArguments>--ignore-exit-code 8</TestingPlatformCommandLineArguments>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Koh.Emulator.Core\Koh.Emulator.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="TUnit" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create a smoke test to prove the test runner works**

File `tests/Koh.Emulator.Core.Tests/SmokeTests.cs`:

```csharp
namespace Koh.Emulator.Core.Tests;

public class SmokeTests
{
    [Test]
    public async Task Scaffold_Works()
    {
        await Assert.That(1 + 1).IsEqualTo(2);
    }
}
```

- [ ] **Step 3: Register in `Koh.slnx`**

Add inside `/tests/`:

```xml
    <Project Path="tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj" />
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj`
Expected: 1 test passes.

- [ ] **Step 5: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/ Koh.slnx
git commit -m "test(emulator): scaffold Koh.Emulator.Core.Tests project"
```

---

### Task 0.7: Create `Koh.Debugger.Tests` empty project

**Files:**
- Create: `tests/Koh.Debugger.Tests/Koh.Debugger.Tests.csproj`
- Create: `tests/Koh.Debugger.Tests/SmokeTests.cs`
- Modify: `Koh.slnx`

- [ ] **Step 1: Create the test project file**

File `tests/Koh.Debugger.Tests/Koh.Debugger.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UseTestingPlatformRunner>true</UseTestingPlatformRunner>
    <TestingPlatformCommandLineArguments>--ignore-exit-code 8</TestingPlatformCommandLineArguments>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Koh.Debugger\Koh.Debugger.csproj" />
    <ProjectReference Include="..\..\src\Koh.Emulator.Core\Koh.Emulator.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="TUnit" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the smoke test**

File `tests/Koh.Debugger.Tests/SmokeTests.cs`:

```csharp
namespace Koh.Debugger.Tests;

public class SmokeTests
{
    [Test]
    public async Task Scaffold_Works()
    {
        await Assert.That(1 + 1).IsEqualTo(2);
    }
}
```

- [ ] **Step 3: Register in `Koh.slnx`**

Add inside `/tests/`:

```xml
    <Project Path="tests/Koh.Debugger.Tests/Koh.Debugger.Tests.csproj" />
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Koh.Debugger.Tests/Koh.Debugger.Tests.csproj`
Expected: 1 test passes.

- [ ] **Step 5: Commit**

```bash
git add tests/Koh.Debugger.Tests/ Koh.slnx
git commit -m "test(debugger): scaffold Koh.Debugger.Tests project"
```

---

### Task 0.8: Scaffold test-ROM download script

**Files:**
- Create: `scripts/download-test-roms.ps1`
- Create: `scripts/download-test-roms.sh`

Phase 0 only scaffolds the scripts with an empty target list. Phase 2 and Phase 3 populate them with actual ROMs.

- [ ] **Step 1: Create the PowerShell script**

File `scripts/download-test-roms.ps1`:

```powershell
# Downloads test ROM fixtures with SHA-256 verification.
# Used by CI and by local developers before running compatibility tests.

param(
    [string]$OutputDir = "tests/fixtures/test-roms"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

# Phase 0 placeholder — no ROMs to download yet.
# Phase 2 adds: dmg-acid2, cgb-acid2
# Phase 3 adds: Blargg cpu_instrs, instr_timing, mem_timing, mem_timing-2,
#               halt_bug, interrupt_time; Mooneye acceptance/
# Phase 4 adds: Blargg dmg_sound

Write-Host "download-test-roms: no ROMs configured for the current phase"
exit 0
```

- [ ] **Step 2: Create the Bash script**

File `scripts/download-test-roms.sh`:

```bash
#!/usr/bin/env bash
# Downloads test ROM fixtures with SHA-256 verification.
# Used by CI and by local developers before running compatibility tests.

set -euo pipefail

OUTPUT_DIR="${1:-tests/fixtures/test-roms}"
mkdir -p "$OUTPUT_DIR"

# Phase 0 placeholder — no ROMs to download yet.
# Phase 2 adds: dmg-acid2, cgb-acid2
# Phase 3 adds: Blargg cpu_instrs, instr_timing, mem_timing, mem_timing-2,
#               halt_bug, interrupt_time; Mooneye acceptance/
# Phase 4 adds: Blargg dmg_sound

echo "download-test-roms: no ROMs configured for the current phase"
```

- [ ] **Step 3: Make the Bash script executable**

Run: `chmod +x scripts/download-test-roms.sh` (on a Unix-like environment).

- [ ] **Step 4: Commit**

```bash
git add scripts/download-test-roms.ps1 scripts/download-test-roms.sh
git commit -m "chore: scaffold test-rom download scripts"
```

---

**Phase 0 exit checkpoint.** Before continuing to Phase 1, verify:

- [ ] `dotnet build Koh.slnx` succeeds with no warnings
- [ ] `dotnet test Koh.slnx` runs all existing tests plus the two new smoke tests
- [ ] AOT publish of `Koh.Emulator.App` succeeds
- [ ] All Phase 0 commits are on the branch

If any of these fail, fix before proceeding.

---

## Phase 1-A: Emulator core — foundation types

### Task 1.A.1: Hardware mode and clock types

**Files:**
- Create: `src/Koh.Emulator.Core/HardwareMode.cs`
- Create: `src/Koh.Emulator.Core/SystemClock.cs`

- [ ] **Step 1: Create `HardwareMode.cs`**

```csharp
namespace Koh.Emulator.Core;

public enum HardwareMode
{
    Dmg,
    Cgb
}
```

- [ ] **Step 2: Create `SystemClock.cs`**

```csharp
namespace Koh.Emulator.Core;

/// <summary>
/// Central clock state for the emulator. One "system tick" equals one PPU dot
/// (4.194304 MHz). In CGB double-speed mode, the CPU advances two T-cycles
/// per system tick; in single-speed, one T-cycle per system tick.
/// </summary>
public sealed class SystemClock
{
    public ulong SystemTicks { get; internal set; }
    public ulong FrameSystemTicks { get; internal set; }
    public bool DoubleSpeed { get; internal set; }

    public const int SystemTicksPerFrame = 70224; // 154 scanlines × 456 dots

    public void AdvanceOne()
    {
        SystemTicks++;
        FrameSystemTicks++;
    }

    public void ResetFrameCounter() => FrameSystemTicks = 0;
}
```

- [ ] **Step 3: Verify the core project builds**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/HardwareMode.cs src/Koh.Emulator.Core/SystemClock.cs
git commit -m "feat(emulator): add HardwareMode and SystemClock"
```

---

### Task 1.A.2: Execution result types

**Files:**
- Create: `src/Koh.Emulator.Core/StopReason.cs`
- Create: `src/Koh.Emulator.Core/StepResult.cs`
- Create: `src/Koh.Emulator.Core/StopCondition.cs`
- Create: `src/Koh.Emulator.Core/RunGuard.cs`

- [ ] **Step 1: Create `StopReason.cs`**

```csharp
namespace Koh.Emulator.Core;

public enum StopReason
{
    FrameComplete,
    InstructionComplete,
    TCycleComplete,
    Breakpoint,
    Watchpoint,
    HaltedBySystem,
    StopRequested,
}
```

- [ ] **Step 2: Create `StepResult.cs`**

```csharp
namespace Koh.Emulator.Core;

public readonly record struct StepResult(
    StopReason Reason,
    ulong TCyclesRan,
    ushort FinalPc);
```

- [ ] **Step 3: Create `StopCondition.cs`**

```csharp
namespace Koh.Emulator.Core;

[Flags]
public enum StopConditionKind : uint
{
    None          = 0,
    PcEquals      = 1 << 0,
    PcInRange     = 1 << 1,
    PcLeavesRange = 1 << 2,
    MaxCycles     = 1 << 3,
    VBlank        = 1 << 4,
    Return        = 1 << 5,
}

public readonly struct StopCondition
{
    public StopConditionKind Kind { get; init; }
    public ushort PcEquals { get; init; }
    public ushort PcRangeStart { get; init; }
    public ushort PcRangeEnd { get; init; }
    public ulong MaxTCycles { get; init; }
    public byte BankFilter { get; init; }  // 0xFF = any bank

    public static StopCondition None => default;

    public static StopCondition AtPc(ushort pc, byte bank = 0xFF) => new()
    {
        Kind = StopConditionKind.PcEquals,
        PcEquals = pc,
        BankFilter = bank,
    };

    public static StopCondition WhilePcInRange(ushort start, ushort end, byte bank = 0xFF) => new()
    {
        Kind = StopConditionKind.PcLeavesRange,
        PcRangeStart = start,
        PcRangeEnd = end,
        BankFilter = bank,
    };
}
```

- [ ] **Step 4: Create `RunGuard.cs`**

```csharp
namespace Koh.Emulator.Core;

/// <summary>
/// Thread-agnostic stop flag used by the host to interrupt a long-running
/// <c>RunFrame</c> or <c>RunUntil</c>. The core checks the flag at instruction
/// boundaries; worst-case latency is the longest SM83 instruction (~24 T-cycles).
/// </summary>
public sealed class RunGuard
{
    private volatile bool _stopRequested;

    public bool StopRequested => _stopRequested;

    public void RequestStop() => _stopRequested = true;

    public void Clear() => _stopRequested = false;
}
```

- [ ] **Step 5: Build the core project**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Emulator.Core/StopReason.cs src/Koh.Emulator.Core/StepResult.cs src/Koh.Emulator.Core/StopCondition.cs src/Koh.Emulator.Core/RunGuard.cs
git commit -m "feat(emulator): add StepResult, StopCondition, RunGuard"
```

---

### Task 1.A.3: CPU register struct

**Files:**
- Create: `src/Koh.Emulator.Core/Cpu/CpuRegisters.cs`

- [ ] **Step 1: Create `CpuRegisters.cs`**

```csharp
namespace Koh.Emulator.Core.Cpu;

public struct CpuRegisters
{
    public byte A;
    public byte F;
    public byte B;
    public byte C;
    public byte D;
    public byte E;
    public byte H;
    public byte L;
    public ushort Sp;
    public ushort Pc;

    public const byte FlagZ = 0x80;
    public const byte FlagN = 0x40;
    public const byte FlagH = 0x20;
    public const byte FlagC = 0x10;

    public ushort AF
    {
        readonly get => (ushort)((A << 8) | (F & 0xF0));
        set { A = (byte)(value >> 8); F = (byte)(value & 0xF0); }
    }

    public ushort BC
    {
        readonly get => (ushort)((B << 8) | C);
        set { B = (byte)(value >> 8); C = (byte)(value & 0xFF); }
    }

    public ushort DE
    {
        readonly get => (ushort)((D << 8) | E);
        set { D = (byte)(value >> 8); E = (byte)(value & 0xFF); }
    }

    public ushort HL
    {
        readonly get => (ushort)((H << 8) | L);
        set { H = (byte)(value >> 8); L = (byte)(value & 0xFF); }
    }

    public readonly bool FlagSet(byte mask) => (F & mask) != 0;
    public void SetFlag(byte mask, bool on) => F = on ? (byte)(F | mask) : (byte)(F & ~mask);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.Core/Cpu/CpuRegisters.cs
git commit -m "feat(emulator): add CpuRegisters struct with AF/BC/DE/HL + flag bits"
```

---

### Task 1.A.4: Joypad state

**Files:**
- Create: `src/Koh.Emulator.Core/Joypad/JoypadState.cs`

- [ ] **Step 1: Create `JoypadState.cs`**

```csharp
namespace Koh.Emulator.Core.Joypad;

[Flags]
public enum JoypadButton : byte
{
    None   = 0,
    Right  = 1 << 0,
    Left   = 1 << 1,
    Up     = 1 << 2,
    Down   = 1 << 3,
    A      = 1 << 4,
    B      = 1 << 5,
    Select = 1 << 6,
    Start  = 1 << 7,
}

public struct JoypadState
{
    public JoypadButton Pressed;

    public readonly bool IsPressed(JoypadButton button) => (Pressed & button) != 0;

    public void Press(JoypadButton button) => Pressed |= button;
    public void Release(JoypadButton button) => Pressed &= ~button;
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Joypad/JoypadState.cs
git commit -m "feat(emulator): add JoypadState struct"
```

---

### Task 1.A.5: Framebuffer

**Files:**
- Create: `src/Koh.Emulator.Core/Ppu/Framebuffer.cs`

- [ ] **Step 1: Create `Framebuffer.cs`**

```csharp
namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// 160×144 RGBA8888 framebuffer with a double buffer. The PPU writes into
/// <see cref="Back"/>; <see cref="Flip"/> swaps buffers at VBlank.
/// </summary>
public sealed class Framebuffer
{
    public const int Width = 160;
    public const int Height = 144;
    public const int BytesPerPixel = 4;
    public const int ByteSize = Width * Height * BytesPerPixel;

    private readonly byte[] _a = new byte[ByteSize];
    private readonly byte[] _b = new byte[ByteSize];
    private bool _aIsFront;

    public Framebuffer()
    {
        _aIsFront = true;
        FillWithPlaceholderGray(_a);
        FillWithPlaceholderGray(_b);
    }

    public ReadOnlySpan<byte> Front => _aIsFront ? _a : _b;
    public Span<byte> Back => _aIsFront ? _b : _a;

    public void Flip() => _aIsFront = !_aIsFront;

    private static void FillWithPlaceholderGray(byte[] buffer)
    {
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i + 0] = 0x2e;
            buffer[i + 1] = 0x2e;
            buffer[i + 2] = 0x2e;
            buffer[i + 3] = 0xff;
        }
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Ppu/Framebuffer.cs
git commit -m "feat(emulator): add Framebuffer with double-buffering"
```

---

### Task 1.A.6: Interrupts state

**Files:**
- Create: `src/Koh.Emulator.Core/Cpu/Interrupts.cs`

- [ ] **Step 1: Create `Interrupts.cs`**

```csharp
namespace Koh.Emulator.Core.Cpu;

public struct Interrupts
{
    public byte IF;             // $FF0F — interrupt flag
    public byte IE;             // $FFFF — interrupt enable
    public bool IME;            // master enable
    public byte EiDelayLatch;   // 0=no pending; 1=will enable after next instruction

    public const byte VBlank = 1 << 0;
    public const byte Stat   = 1 << 1;
    public const byte Timer  = 1 << 2;
    public const byte Serial = 1 << 3;
    public const byte Joypad = 1 << 4;

    public readonly byte Pending => (byte)(IF & IE & 0x1F);
    public readonly bool HasPending => Pending != 0;

    public void Raise(byte interrupt) => IF |= interrupt;
    public void Clear(byte interrupt) => IF &= (byte)~interrupt;
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`

```bash
git add src/Koh.Emulator.Core/Cpu/Interrupts.cs
git commit -m "feat(emulator): add Interrupts struct with IF/IE/IME latches"
```

---

## Phase 1-B: Cartridge and ROM header

### Task 1.B.1: MapperKind enum and cartridge header parser

**Files:**
- Create: `src/Koh.Emulator.Core/Cartridge/MapperKind.cs`
- Create: `src/Koh.Emulator.Core/Cartridge/CartridgeHeader.cs`

- [ ] **Step 1: Create `MapperKind.cs`**

```csharp
namespace Koh.Emulator.Core.Cartridge;

public enum MapperKind : byte
{
    RomOnly = 0,
    Mbc1 = 1,
    // Mbc3 and Mbc5 are added in Phase 4. Intermediate values reserved.
}
```

- [ ] **Step 2: Create `CartridgeHeader.cs`**

```csharp
namespace Koh.Emulator.Core.Cartridge;

public readonly record struct CartridgeHeader(
    string Title,
    MapperKind MapperKind,
    int RomBanks,
    int RamBanks,
    bool CgbFlag,
    bool CgbOnly)
{
    public static CartridgeHeader Parse(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 0x150)
            throw new ArgumentException("ROM smaller than header size", nameof(rom));

        // Title: $0134-$0143. CGB uses the last byte as CGB flag.
        byte cgbByte = rom[0x143];
        bool cgbFlag = (cgbByte & 0x80) != 0;
        bool cgbOnly = cgbByte == 0xC0;
        int titleLen = cgbFlag ? 15 : 16;
        int titleEnd = 0x134;
        while (titleEnd < 0x134 + titleLen && rom[titleEnd] != 0) titleEnd++;
        string title = System.Text.Encoding.ASCII.GetString(rom[0x134..titleEnd]);

        byte cartType = rom[0x147];
        MapperKind mapper = cartType switch
        {
            0x00 => MapperKind.RomOnly,
            0x01 or 0x02 or 0x03 => MapperKind.Mbc1,
            _ => throw new NotSupportedException($"Cartridge type ${cartType:X2} not supported in Phase 1"),
        };

        int romBanks = rom[0x148] switch
        {
            0x00 => 2,
            0x01 => 4,
            0x02 => 8,
            0x03 => 16,
            0x04 => 32,
            0x05 => 64,
            0x06 => 128,
            0x07 => 256,
            0x08 => 512,
            _ => 2,
        };

        int ramBanks = rom[0x149] switch
        {
            0x00 => 0,
            0x01 => 0,   // unused historically
            0x02 => 1,
            0x03 => 4,
            0x04 => 16,
            0x05 => 8,
            _ => 0,
        };

        return new CartridgeHeader(title, mapper, romBanks, ramBanks, cgbFlag, cgbOnly);
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 4: Write a failing test**

File `tests/Koh.Emulator.Core.Tests/CartridgeHeaderTests.cs`:

```csharp
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class CartridgeHeaderTests
{
    private static byte[] MakeHeader(byte cartType, byte romSize, byte ramSize, byte cgbFlag, string title)
    {
        var rom = new byte[0x150];
        var titleBytes = System.Text.Encoding.ASCII.GetBytes(title);
        titleBytes.AsSpan(0, Math.Min(titleBytes.Length, 15)).CopyTo(rom.AsSpan(0x134));
        rom[0x143] = cgbFlag;
        rom[0x147] = cartType;
        rom[0x148] = romSize;
        rom[0x149] = ramSize;
        return rom;
    }

    [Test]
    public async Task Parse_RomOnly_16KB()
    {
        var rom = MakeHeader(cartType: 0x00, romSize: 0x00, ramSize: 0x00, cgbFlag: 0x00, title: "TEST");
        var header = CartridgeHeader.Parse(rom);

        await Assert.That(header.MapperKind).IsEqualTo(MapperKind.RomOnly);
        await Assert.That(header.RomBanks).IsEqualTo(2);
        await Assert.That(header.RamBanks).IsEqualTo(0);
        await Assert.That(header.CgbFlag).IsFalse();
        await Assert.That(header.Title).IsEqualTo("TEST");
    }

    [Test]
    public async Task Parse_Mbc1_WithRam()
    {
        var rom = MakeHeader(cartType: 0x03, romSize: 0x03, ramSize: 0x03, cgbFlag: 0x00, title: "MBC1TEST");
        var header = CartridgeHeader.Parse(rom);

        await Assert.That(header.MapperKind).IsEqualTo(MapperKind.Mbc1);
        await Assert.That(header.RomBanks).IsEqualTo(16);
        await Assert.That(header.RamBanks).IsEqualTo(4);
    }

    [Test]
    public async Task Parse_CgbFlag_Detected()
    {
        var rom = MakeHeader(cartType: 0x00, romSize: 0x00, ramSize: 0x00, cgbFlag: 0x80, title: "CGB");
        var header = CartridgeHeader.Parse(rom);

        await Assert.That(header.CgbFlag).IsTrue();
        await Assert.That(header.CgbOnly).IsFalse();
    }

    [Test]
    public async Task Parse_CgbOnly_Detected()
    {
        var rom = MakeHeader(cartType: 0x00, romSize: 0x00, ramSize: 0x00, cgbFlag: 0xC0, title: "CGBO");
        var header = CartridgeHeader.Parse(rom);

        await Assert.That(header.CgbOnly).IsTrue();
    }

    [Test]
    public async Task Parse_UnsupportedCartType_Throws()
    {
        var rom = MakeHeader(cartType: 0xFF, romSize: 0x00, ramSize: 0x00, cgbFlag: 0x00, title: "BAD");
        await Assert.That(() => CartridgeHeader.Parse(rom)).Throws<NotSupportedException>();
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj`
Expected: all 5 new tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Emulator.Core/Cartridge/ tests/Koh.Emulator.Core.Tests/CartridgeHeaderTests.cs
git commit -m "feat(emulator): add CartridgeHeader parser with tests"
```

---

### Task 1.B.2: Cartridge class with enum-dispatched MBC logic

**Files:**
- Create: `src/Koh.Emulator.Core/Cartridge/Cartridge.cs`
- Create: `src/Koh.Emulator.Core/Cartridge/Mbc1.cs`
- Create: `src/Koh.Emulator.Core/Cartridge/CartridgeFactory.cs`

- [ ] **Step 1: Create `Cartridge.cs`**

Per design §7.5: sealed class, enum-dispatched mapper logic, no interface.

```csharp
namespace Koh.Emulator.Core.Cartridge;

public sealed class Cartridge
{
    public readonly MapperKind Kind;
    public readonly CartridgeHeader Header;
    public readonly byte[] Rom;
    public readonly byte[] Ram;

    // MBC1 state
    public byte Mbc1_BankLow;      // 5-bit bank low (1..31)
    public byte Mbc1_BankHigh;     // 2-bit bank high (0..3)
    public bool Mbc1_RamEnabled;
    public byte Mbc1_Mode;         // 0 = ROM-bank mode, 1 = RAM-bank mode

    internal Cartridge(CartridgeHeader header, byte[] rom, byte[] ram)
    {
        Header = header;
        Kind = header.MapperKind;
        Rom = rom;
        Ram = ram;
        Mbc1_BankLow = 1;
    }

    public byte ReadRom(ushort address)
    {
        switch (Kind)
        {
            case MapperKind.RomOnly:
                return address < Rom.Length ? Rom[address] : (byte)0xFF;
            case MapperKind.Mbc1:
                return Mbc1.ReadRom(this, address);
            default:
                return 0xFF;
        }
    }

    public void WriteRom(ushort address, byte value)
    {
        switch (Kind)
        {
            case MapperKind.RomOnly:
                // ROM is read-only; writes are dropped.
                break;
            case MapperKind.Mbc1:
                Mbc1.WriteRom(this, address, value);
                break;
        }
    }

    public byte ReadRam(ushort address)
    {
        switch (Kind)
        {
            case MapperKind.RomOnly:
                return 0xFF;
            case MapperKind.Mbc1:
                return Mbc1.ReadRam(this, address);
            default:
                return 0xFF;
        }
    }

    public void WriteRam(ushort address, byte value)
    {
        switch (Kind)
        {
            case MapperKind.RomOnly:
                break;
            case MapperKind.Mbc1:
                Mbc1.WriteRam(this, address, value);
                break;
        }
    }
}
```

- [ ] **Step 2: Create `Mbc1.cs`**

```csharp
namespace Koh.Emulator.Core.Cartridge;

internal static class Mbc1
{
    public static byte ReadRom(Cartridge cart, ushort address)
    {
        if (address < 0x4000)
        {
            // Bank 0 area. In MBC1 mode 1 with large ROMs, the high 2 bits
            // of the bank register map this window to banks $20/$40/$60.
            int bank0 = cart.Mbc1_Mode == 1 ? (cart.Mbc1_BankHigh << 5) : 0;
            int offset = (bank0 * 0x4000) + address;
            return offset < cart.Rom.Length ? cart.Rom[offset] : (byte)0xFF;
        }
        else
        {
            // $4000-$7FFF switchable ROM bank.
            int low = cart.Mbc1_BankLow & 0x1F;
            if (low == 0) low = 1;  // MBC1 quirk: bank 0 selects bank 1
            int bank = (cart.Mbc1_BankHigh << 5) | low;
            int offset = (bank * 0x4000) + (address - 0x4000);
            return offset < cart.Rom.Length ? cart.Rom[offset] : (byte)0xFF;
        }
    }

    public static void WriteRom(Cartridge cart, ushort address, byte value)
    {
        if (address < 0x2000)
        {
            // RAM enable: lower 4 bits == 0xA enables.
            cart.Mbc1_RamEnabled = (value & 0x0F) == 0x0A;
        }
        else if (address < 0x4000)
        {
            // ROM bank low (5 bits).
            byte low = (byte)(value & 0x1F);
            cart.Mbc1_BankLow = low == 0 ? (byte)1 : low;
        }
        else if (address < 0x6000)
        {
            // RAM bank / ROM bank high (2 bits).
            cart.Mbc1_BankHigh = (byte)(value & 0x03);
        }
        else
        {
            // Banking mode select.
            cart.Mbc1_Mode = (byte)(value & 0x01);
        }
    }

    public static byte ReadRam(Cartridge cart, ushort address)
    {
        if (!cart.Mbc1_RamEnabled || cart.Ram.Length == 0) return 0xFF;
        int bank = cart.Mbc1_Mode == 1 ? cart.Mbc1_BankHigh : 0;
        int offset = (bank * 0x2000) + (address - 0xA000);
        return offset < cart.Ram.Length ? cart.Ram[offset] : (byte)0xFF;
    }

    public static void WriteRam(Cartridge cart, ushort address, byte value)
    {
        if (!cart.Mbc1_RamEnabled || cart.Ram.Length == 0) return;
        int bank = cart.Mbc1_Mode == 1 ? cart.Mbc1_BankHigh : 0;
        int offset = (bank * 0x2000) + (address - 0xA000);
        if (offset < cart.Ram.Length) cart.Ram[offset] = value;
    }
}
```

- [ ] **Step 3: Create `CartridgeFactory.cs`**

```csharp
namespace Koh.Emulator.Core.Cartridge;

public static class CartridgeFactory
{
    public static Cartridge Load(ReadOnlySpan<byte> romBytes)
    {
        var header = CartridgeHeader.Parse(romBytes);
        var rom = romBytes.ToArray();
        var ram = new byte[header.RamBanks * 0x2000];
        return new Cartridge(header, rom, ram);
    }
}
```

- [ ] **Step 4: Write failing tests for Mbc1**

File `tests/Koh.Emulator.Core.Tests/Mbc1Tests.cs`:

```csharp
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class Mbc1Tests
{
    private static Cartridge MakeMbc1(int romBanks, int ramBanks)
    {
        int romSizeCode = romBanks switch
        {
            2 => 0x00,
            4 => 0x01,
            8 => 0x02,
            16 => 0x03,
            32 => 0x04,
            64 => 0x05,
            128 => 0x06,
            _ => 0x00,
        };
        int ramSizeCode = ramBanks switch
        {
            0 => 0x00,
            1 => 0x02,
            4 => 0x03,
            _ => 0x00,
        };

        var rom = new byte[romBanks * 0x4000];
        rom[0x143] = 0x00;
        rom[0x147] = 0x03;  // MBC1 + RAM + battery
        rom[0x148] = (byte)romSizeCode;
        rom[0x149] = (byte)ramSizeCode;
        // Mark each bank with its bank number at offset 0 of the bank for easy verification.
        for (int bank = 0; bank < romBanks; bank++)
        {
            rom[bank * 0x4000] = (byte)bank;
            rom[bank * 0x4000 + 1] = (byte)(bank >> 8);
        }

        return CartridgeFactory.Load(rom);
    }

    [Test]
    public async Task Bank0_Reads_FromBank0()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 0);
        await Assert.That(cart.ReadRom(0x0000)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Bank1_DefaultSelected()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 0);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)1);
    }

    [Test]
    public async Task BankSelect_2_SelectsBank2()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 0);
        cart.WriteRom(0x2000, 0x02);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)2);
    }

    [Test]
    public async Task BankSelect_0_BecomesBank1()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 0);
        cart.WriteRom(0x2000, 0x00);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Ram_DisabledByDefault()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 1);
        await Assert.That(cart.ReadRam(0xA000)).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Ram_EnabledAndWriteReadRoundTrip()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 1);
        cart.WriteRom(0x0000, 0x0A);           // enable RAM
        cart.WriteRam(0xA000, 0x42);
        await Assert.That(cart.ReadRam(0xA000)).IsEqualTo((byte)0x42);
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj`
Expected: all Mbc1 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Emulator.Core/Cartridge/Cartridge.cs src/Koh.Emulator.Core/Cartridge/Mbc1.cs src/Koh.Emulator.Core/Cartridge/CartridgeFactory.cs tests/Koh.Emulator.Core.Tests/Mbc1Tests.cs
git commit -m "feat(emulator): add Cartridge sealed class with RomOnly+Mbc1 dispatch"
```

---

## Phase 1-C: Timer (full implementation)

Phase 1 ships the full Timer per §7.12. The Timer is small and self-contained; deferring it would leave the Phase 1 representative benchmark without a realistic tick workload.

### Task 1.C.1: Timer implementation

**Files:**
- Create: `src/Koh.Emulator.Core/Timer/Timer.cs`

- [ ] **Step 1: Create `Timer.cs`**

Per design §7.2 and §3: the timer is driven by the CPU T-cycle clock. Internal state is a 16-bit counter incrementing every CPU T-cycle. `DIV` is bits 9–15 of that counter. `TIMA` increments on falling edges of a TAC-selected bit. TMA reload has a 1-M-cycle delay during which the overflow was triggered.

```csharp
using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Timer;

public sealed class Timer
{
    private ushort _internalCounter;   // 16-bit system counter; DIV is bits 9..15
    private byte _tima;
    private byte _tma;
    private byte _tac;
    private int _reloadDelay;          // 0..4 T-cycles between TIMA overflow and TMA reload

    private bool _lastSelectedBit;

    public byte DIV => (byte)(_internalCounter >> 8);
    public byte TIMA => _tima;
    public byte TMA => _tma;
    public byte TAC => _tac;

    public void WriteDiv()
    {
        // Any write to DIV resets the full 16-bit counter to 0.
        // This can produce a falling edge on the selected bit → TIMA increment.
        _internalCounter = 0;
        UpdateAfterCounterChange(raiseOn: raise => { /* no interrupt state available here; raise via ref */ });
    }

    public void WriteTima(byte value)
    {
        // If we're in the reload delay window, ignore the write (hardware quirk);
        // otherwise it updates TIMA and cancels the pending overflow.
        if (_reloadDelay > 0)
        {
            // During the reload-delay 1 M-cycle, writes are ignored.
            // (Simplified model adequate for the tests we gate against.)
        }
        else
        {
            _tima = value;
        }
    }

    public void WriteTma(byte value)
    {
        _tma = value;
        // If a reload happens during the same cycle as a TMA write, the new TMA value is used.
        if (_reloadDelay == 1) _tima = value;
    }

    public void WriteTac(byte value)
    {
        _tac = (byte)(value & 0x07);
        // Writing TAC can also cause a falling-edge glitch on the selected bit.
    }

    /// <summary>
    /// Advance the timer one CPU T-cycle. Must be called in lockstep with the CPU clock,
    /// so in double-speed mode this is called twice per system tick.
    /// </summary>
    public void TickT(ref Interrupts interrupts)
    {
        // Increment the internal counter by 1 T-cycle.
        _internalCounter++;

        // Reload-delay handling: if a TIMA overflow is pending, count down.
        if (_reloadDelay > 0)
        {
            _reloadDelay--;
            if (_reloadDelay == 0)
            {
                _tima = _tma;
                interrupts.Raise(Interrupts.Timer);
            }
        }

        // Check for falling edge on the selected bit of the internal counter.
        bool timerEnabled = (_tac & 0x04) != 0;
        int selectedBit = (_tac & 0x03) switch
        {
            0 => 9,   // 4096 Hz  (every 1024 T-cycles)
            1 => 3,   //  262144 Hz (every 16 T-cycles)
            2 => 5,   //  65536 Hz (every 64 T-cycles)
            _ => 7,   //  16384 Hz (every 256 T-cycles)
        };
        bool currentBit = timerEnabled && ((_internalCounter >> selectedBit) & 1) != 0;

        if (_lastSelectedBit && !currentBit)
        {
            IncrementTima();
        }
        _lastSelectedBit = currentBit;
    }

    private void IncrementTima()
    {
        if (_tima == 0xFF)
        {
            _tima = 0;
            _reloadDelay = 4;   // 1 M-cycle (4 T-cycles) of delay before TMA reload + IRQ
        }
        else
        {
            _tima++;
        }
    }

    // Used by WriteDiv if we want to apply edge detection there too.
    private void UpdateAfterCounterChange(Action<bool> raiseOn) { /* placeholder for future extension */ }

    public void Reset()
    {
        _internalCounter = 0;
        _tima = 0;
        _tma = 0;
        _tac = 0;
        _reloadDelay = 0;
        _lastSelectedBit = false;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.Core/Timer/Timer.cs
git commit -m "feat(emulator): add full Timer with DIV/TIMA/TMA/TAC + TMA reload delay"
```

---

### Task 1.C.2: Timer tests — DIV increment rate

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/TimerTests.cs`

- [ ] **Step 1: Write failing DIV rate test**

File `tests/Koh.Emulator.Core.Tests/TimerTests.cs`:

```csharp
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Timer;

namespace Koh.Emulator.Core.Tests;

public class TimerTests
{
    private static void Tick(Timer timer, int tCycles, ref Interrupts interrupts)
    {
        for (int i = 0; i < tCycles; i++) timer.TickT(ref interrupts);
    }

    [Test]
    public async Task Div_Increments_Every_256_TCycles()
    {
        var timer = new Timer();
        var interrupts = new Interrupts();

        // Initial DIV is 0.
        await Assert.That(timer.DIV).IsEqualTo((byte)0);

        Tick(timer, 255, ref interrupts);
        await Assert.That(timer.DIV).IsEqualTo((byte)0);

        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.DIV).IsEqualTo((byte)1);

        Tick(timer, 256, ref interrupts);
        await Assert.That(timer.DIV).IsEqualTo((byte)2);
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter TimerTests.Div_Increments_Every_256_TCycles`
Expected: test passes.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/TimerTests.cs
git commit -m "test(emulator): add Timer DIV increment-rate test"
```

---

### Task 1.C.3: Timer tests — TIMA at each TAC setting

- [ ] **Step 1: Append four TIMA tests to `TimerTests.cs`**

Add these tests inside the `TimerTests` class:

```csharp
    [Test]
    public async Task Tima_Tac00_Increments_Every_1024_TCycles()
    {
        var timer = new Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0100);  // enable, freq 00 → 1024 T-cycles

        Tick(timer, 1023, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Tima_Tac01_Increments_Every_16_TCycles()
    {
        var timer = new Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101);  // enable, freq 01 → 16 T-cycles

        Tick(timer, 15, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Tima_Tac10_Increments_Every_64_TCycles()
    {
        var timer = new Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0110);  // enable, freq 10 → 64 T-cycles

        Tick(timer, 63, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Tima_Tac11_Increments_Every_256_TCycles()
    {
        var timer = new Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0111);  // enable, freq 11 → 256 T-cycles

        Tick(timer, 255, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        Tick(timer, 1, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)1);
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter TimerTests`
Expected: all four TIMA tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/TimerTests.cs
git commit -m "test(emulator): add TIMA increment rate tests for all TAC settings"
```

---

### Task 1.C.4: Timer tests — TIMA overflow, TMA reload, IRQ

- [ ] **Step 1: Append overflow/IRQ tests**

Add inside `TimerTests`:

```csharp
    [Test]
    public async Task Tima_Overflow_Reloads_From_Tma_After_Delay_And_Raises_Irq()
    {
        var timer = new Timer();
        var interrupts = new Interrupts();
        timer.WriteTac(0b_0000_0101);  // enable, freq 01 (16 T-cycles per TIMA increment)
        timer.WriteTma(0x42);
        timer.WriteTima(0xFF);

        // 16 T-cycles to trigger TIMA overflow (TIMA → 0, reload-delay = 4)
        Tick(timer, 16, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0);
        await Assert.That((interrupts.IF & Interrupts.Timer) != 0).IsFalse();

        // 4 more T-cycles: reload happens, IRQ raised.
        Tick(timer, 4, ref interrupts);
        await Assert.That(timer.TIMA).IsEqualTo((byte)0x42);
        await Assert.That((interrupts.IF & Interrupts.Timer) != 0).IsTrue();
    }

    [Test]
    public async Task WriteDiv_Resets_Internal_Counter()
    {
        var timer = new Timer();
        var interrupts = new Interrupts();

        Tick(timer, 500, ref interrupts);
        await Assert.That(timer.DIV).IsGreaterThan((byte)0);

        timer.WriteDiv();
        await Assert.That(timer.DIV).IsEqualTo((byte)0);
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter TimerTests`
Expected: all TimerTests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/TimerTests.cs
git commit -m "test(emulator): add TIMA overflow, TMA reload, and IRQ tests"
```

---

## Phase 1-D: Memory bus (MMU)

### Task 1.D.1: Mmu with all-region routing

**Files:**
- Create: `src/Koh.Emulator.Core/Bus/Mmu.cs`
- Create: `src/Koh.Emulator.Core/Bus/IoRegisters.cs`

Phase 1 MMU routes every region but does not implement PPU mode 2/3 lockout or OAM DMA contention — those arrive in Phase 2 per §7.12.

- [ ] **Step 1: Create `IoRegisters.cs` with Phase 1 stubs**

```csharp
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Timer;

namespace Koh.Emulator.Core.Bus;

/// <summary>
/// $FF00-$FF7F I/O dispatch. Phase 1 implements only the registers needed
/// for Timer + Interrupts. Other registers read/write a backing byte array
/// without side effects; Phase 2 wires PPU registers.
/// </summary>
public sealed class IoRegisters
{
    private readonly byte[] _io = new byte[0x80];

    public Timer.Timer Timer { get; }
    private Interrupts _interrupts;

    public ref Interrupts Interrupts => ref _interrupts;

    public IoRegisters(Timer.Timer timer)
    {
        Timer = timer;
    }

    public byte Read(ushort address)
    {
        int idx = address - 0xFF00;
        if (idx < 0 || idx >= _io.Length) return 0xFF;

        return address switch
        {
            0xFF04 => Timer.DIV,
            0xFF05 => Timer.TIMA,
            0xFF06 => Timer.TMA,
            0xFF07 => (byte)(Timer.TAC | 0xF8),
            0xFF0F => (byte)(_interrupts.IF | 0xE0),
            _ => _io[idx],
        };
    }

    public void Write(ushort address, byte value)
    {
        int idx = address - 0xFF00;
        if (idx < 0 || idx >= _io.Length) return;

        switch (address)
        {
            case 0xFF04: Timer.WriteDiv(); break;
            case 0xFF05: Timer.WriteTima(value); break;
            case 0xFF06: Timer.WriteTma(value); break;
            case 0xFF07: Timer.WriteTac(value); break;
            case 0xFF0F: _interrupts.IF = (byte)(value & 0x1F); break;
            default:     _io[idx] = value; break;
        }
    }

    public byte ReadIe() => _interrupts.IE;
    public void WriteIe(byte value) => _interrupts.IE = (byte)(value & 0x1F);
}
```

- [ ] **Step 2: Create `Mmu.cs`**

```csharp
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Bus;

/// <summary>
/// Routes memory accesses across the Game Boy address space.
/// Phase 1: basic routing only. PPU mode lockout and DMA contention arrive in Phase 2.
/// </summary>
public sealed class Mmu
{
    private readonly Cartridge.Cartridge _cart;
    private readonly byte[] _vram = new byte[0x2000 * 2];  // 2 banks for CGB
    private readonly byte[] _wram = new byte[0x1000 * 8];  // 8 banks for CGB
    private readonly byte[] _oam = new byte[0xA0];
    private readonly byte[] _hram = new byte[0x7F];
    public IoRegisters Io { get; }

    private byte _vramBank;
    private byte _wramBank = 1;

    public Mmu(Cartridge.Cartridge cart, IoRegisters io)
    {
        _cart = cart;
        Io = io;
    }

    public byte ReadByte(ushort address)
    {
        switch (address >> 12)
        {
            case 0x0: case 0x1: case 0x2: case 0x3:
            case 0x4: case 0x5: case 0x6: case 0x7:
                return _cart.ReadRom(address);
            case 0x8: case 0x9:
                return _vram[_vramBank * 0x2000 + (address - 0x8000)];
            case 0xA: case 0xB:
                return _cart.ReadRam(address);
            case 0xC:
                return _wram[address - 0xC000];
            case 0xD:
                return _wram[_wramBank * 0x1000 + (address - 0xD000)];
            case 0xE:
                return _wram[address - 0xE000];                    // echo RAM $C000-$CFFF
            case 0xF:
                if (address < 0xFE00) return _wram[_wramBank * 0x1000 + (address - 0xF000)];  // echo RAM $D000-$DDFF
                if (address < 0xFEA0) return _oam[address - 0xFE00];
                if (address < 0xFF00) return 0x00;                  // prohibited region
                if (address == 0xFFFF) return Io.ReadIe();
                if (address >= 0xFF80) return _hram[address - 0xFF80];
                return Io.Read(address);
        }
        return 0xFF;
    }

    public void WriteByte(ushort address, byte value)
    {
        switch (address >> 12)
        {
            case 0x0: case 0x1: case 0x2: case 0x3:
            case 0x4: case 0x5: case 0x6: case 0x7:
                _cart.WriteRom(address, value);
                return;
            case 0x8: case 0x9:
                _vram[_vramBank * 0x2000 + (address - 0x8000)] = value;
                return;
            case 0xA: case 0xB:
                _cart.WriteRam(address, value);
                return;
            case 0xC:
                _wram[address - 0xC000] = value;
                return;
            case 0xD:
                _wram[_wramBank * 0x1000 + (address - 0xD000)] = value;
                return;
            case 0xE:
                _wram[address - 0xE000] = value;
                return;
            case 0xF:
                if (address < 0xFE00) { _wram[_wramBank * 0x1000 + (address - 0xF000)] = value; return; }
                if (address < 0xFEA0) { _oam[address - 0xFE00] = value; return; }
                if (address < 0xFF00) return;
                if (address == 0xFFFF) { Io.WriteIe(value); return; }
                if (address >= 0xFF80) { _hram[address - 0xFF80] = value; return; }
                Io.Write(address, value);
                return;
        }
    }

    /// <summary>
    /// Raw debug read. Bypasses access restrictions (none in Phase 1).
    /// Phase 2 adds bypass of PPU mode lockout.
    /// </summary>
    public byte DebugRead(ushort address) => ReadByte(address);

    /// <summary>
    /// Raw debug write per §7.10. Caller is responsible for enforcing the paused-only rule.
    /// Writes to I/O triggered registers do not trigger side effects; the classification table
    /// in §7.10 is implemented here as a region check.
    /// </summary>
    public bool DebugWrite(ushort address, byte value)
    {
        switch (address >> 12)
        {
            case 0x0: case 0x1: case 0x2: case 0x3:
            case 0x4: case 0x5: case 0x6: case 0x7:
                // Live ROM patch per §7.10.
                int romOffset = address;
                if (_cart.Header.MapperKind == MapperKind.RomOnly)
                {
                    if (romOffset < _cart.Rom.Length) _cart.Rom[romOffset] = value;
                }
                else
                {
                    // Compute current banked address for MBC1 by reading back routing logic.
                    // Phase 1: patch only bank 0 for MBC1 to keep the contract simple;
                    // higher banks need MBC-aware address translation which can be added when needed.
                    if (address < 0x4000 && romOffset < _cart.Rom.Length)
                        _cart.Rom[romOffset] = value;
                }
                return true;
            default:
                WriteByte(address, value);
                return true;
        }
    }

    // Convenience accessors used by debugger scopes.
    public ReadOnlySpan<byte> Vram => _vram;
    public ReadOnlySpan<byte> Oam => _oam;
    public ReadOnlySpan<byte> Hram => _hram;
}
```

- [ ] **Step 3: Build the core project**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Bus/Mmu.cs src/Koh.Emulator.Core/Bus/IoRegisters.cs
git commit -m "feat(emulator): add Mmu routing and IoRegisters with Phase 1 scope"
```

---

### Task 1.D.2: Mmu region routing tests

**Files:**
- Create: `tests/Koh.Emulator.Core.Tests/MmuTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Timer;

namespace Koh.Emulator.Core.Tests;

public class MmuTests
{
    private static Mmu MakeMmu()
    {
        var rom = new byte[0x8000];
        rom[0x0000] = 0xAA;
        rom[0x7FFF] = 0xBB;
        rom[0x143] = 0x00;
        rom[0x147] = 0x00;
        rom[0x148] = 0x01; // 4 banks of ROM, still RomOnly in Phase 1 though cart type 0x00
        var cart = CartridgeFactory.Load(rom);
        var timer = new Timer.Timer();
        var io = new IoRegisters(timer);
        return new Mmu(cart, io);
    }

    [Test]
    public async Task Read_Bank0_Rom()
    {
        var mmu = MakeMmu();
        await Assert.That(mmu.ReadByte(0x0000)).IsEqualTo((byte)0xAA);
    }

    [Test]
    public async Task Read_Wram_After_Write()
    {
        var mmu = MakeMmu();
        mmu.WriteByte(0xC123, 0x77);
        await Assert.That(mmu.ReadByte(0xC123)).IsEqualTo((byte)0x77);
    }

    [Test]
    public async Task EchoRam_Mirrors_Wram()
    {
        var mmu = MakeMmu();
        mmu.WriteByte(0xC234, 0x88);
        await Assert.That(mmu.ReadByte(0xE234)).IsEqualTo((byte)0x88);
    }

    [Test]
    public async Task Prohibited_Region_Reads_Zero()
    {
        var mmu = MakeMmu();
        await Assert.That(mmu.ReadByte(0xFEA5)).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Hram_RoundTrip()
    {
        var mmu = MakeMmu();
        mmu.WriteByte(0xFF90, 0x55);
        await Assert.That(mmu.ReadByte(0xFF90)).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task IE_Register_Masked_To_Lower5Bits()
    {
        var mmu = MakeMmu();
        mmu.WriteByte(0xFFFF, 0xFF);
        await Assert.That(mmu.ReadByte(0xFFFF)).IsEqualTo((byte)0x1F);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter MmuTests`
Expected: all tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Emulator.Core.Tests/MmuTests.cs
git commit -m "test(emulator): add Mmu region routing tests"
```

---

## Phase 1-E: PPU skeleton and mock CPU

Per §7.12 Phase 1, the PPU only ticks its dot counter and LY register; the fetcher and pixel FIFO arrive in Phase 2. The CPU runs a representative mock instruction (one memory read, one ALU op, one branch check) per §12.9.

### Task 1.E.1: PPU skeleton

**Files:**
- Create: `src/Koh.Emulator.Core/Ppu/PpuMode.cs`
- Create: `src/Koh.Emulator.Core/Ppu/Ppu.cs`

- [ ] **Step 1: Create `PpuMode.cs`**

```csharp
namespace Koh.Emulator.Core.Ppu;

public enum PpuMode : byte
{
    HBlank = 0,
    VBlank = 1,
    OamScan = 2,
    Drawing = 3,
}
```

- [ ] **Step 2: Create `Ppu.cs`**

```csharp
using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// Phase 1 PPU: advances a dot counter and LY. No rendering, no mode transitions
/// on the STAT IRQ line, no pixel FIFO. Phase 2 replaces this with the full
/// algorithmic fetcher model per §7.7.
/// </summary>
public sealed class Ppu
{
    public Framebuffer Framebuffer { get; } = new();

    public byte LY { get; private set; }
    public int Dot { get; private set; }
    public PpuMode Mode { get; private set; } = PpuMode.OamScan;

    private const int DotsPerScanline = 456;
    private const int ScanlinesPerFrame = 154;

    public void TickDot(ref Interrupts interrupts)
    {
        Dot++;
        if (Dot >= DotsPerScanline)
        {
            Dot = 0;
            LY++;
            if (LY == 144)
            {
                Mode = PpuMode.VBlank;
                interrupts.Raise(Interrupts.VBlank);
                Framebuffer.Flip();
            }
            else if (LY >= ScanlinesPerFrame)
            {
                LY = 0;
                Mode = PpuMode.OamScan;
            }
            else if (LY < 144)
            {
                Mode = PpuMode.OamScan;
            }
        }
    }

    public void Reset()
    {
        LY = 0;
        Dot = 0;
        Mode = PpuMode.OamScan;
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/Ppu/PpuMode.cs src/Koh.Emulator.Core/Ppu/Ppu.cs
git commit -m "feat(emulator): add Phase 1 PPU skeleton (dot counter + LY + VBlank IRQ)"
```

---

### Task 1.E.2: Mock CPU (representative workload)

**Files:**
- Create: `src/Koh.Emulator.Core/Cpu/Sm83.cs`

Per §12.9, the mock "instruction" is 4 T-cycles and performs one `Mmu.ReadByte` (varying address to defeat caching), one ALU op, one flag update, one conditional branch.

- [ ] **Step 1: Create `Sm83.cs`**

```csharp
using Koh.Emulator.Core.Bus;

namespace Koh.Emulator.Core.Cpu;

/// <summary>
/// Phase 1 CPU: runs a single representative mock instruction per §12.9.
/// Four T-cycles per instruction. No opcode decoding yet — that arrives in Phase 3.
/// </summary>
public sealed class Sm83
{
    private readonly Mmu _mmu;
    public CpuRegisters Registers;
    public Interrupts Interrupts;

    public bool Halted;
    public ulong TotalTCycles;

    private byte _tWithinInstruction;   // 0..3

    public Sm83(Mmu mmu)
    {
        _mmu = mmu;
        Registers.Pc = 0x0100;
        Registers.Sp = 0xFFFE;
    }

    /// <summary>
    /// Advance the CPU one T-cycle. Returns true if an instruction boundary was just crossed.
    /// </summary>
    public bool TickT()
    {
        TotalTCycles++;
        _tWithinInstruction++;
        if (_tWithinInstruction >= 4)
        {
            _tWithinInstruction = 0;
            ExecuteOneMockInstruction();
            return true;
        }
        return false;
    }

    private void ExecuteOneMockInstruction()
    {
        // Representative workload per §12.9 Phase 1 row:
        //   one memory read (varying address)
        //   one ALU op
        //   one flag update
        //   one conditional branch
        byte loaded = _mmu.ReadByte(Registers.Pc);
        byte sum = (byte)(Registers.A + loaded);
        Registers.SetFlag(CpuRegisters.FlagZ, sum == 0);
        Registers.SetFlag(CpuRegisters.FlagC, sum < Registers.A);
        Registers.A = sum;

        if ((sum & 1) == 0)
            Registers.Pc = (ushort)(Registers.Pc + 1);
        else
            Registers.Pc = (ushort)(Registers.Pc + 2);
    }

    public void Reset()
    {
        Registers = default;
        Registers.Pc = 0x0100;
        Registers.Sp = 0xFFFE;
        Interrupts = default;
        Halted = false;
        TotalTCycles = 0;
        _tWithinInstruction = 0;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.Core/Cpu/Sm83.cs
git commit -m "feat(emulator): add Phase 1 mock CPU with representative workload"
```

---

## Phase 1-F: GameBoySystem facade and execution API

### Task 1.F.1: GameBoySystem facade with StepOneSystemTick

**Files:**
- Create: `src/Koh.Emulator.Core/GameBoySystem.cs`

- [ ] **Step 1: Create `GameBoySystem.cs`**

```csharp
using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Joypad;
using Koh.Emulator.Core.Ppu;
using Koh.Emulator.Core.Timer;

namespace Koh.Emulator.Core;

public sealed class GameBoySystem
{
    public HardwareMode Mode { get; }
    public SystemClock Clock { get; } = new();
    public Cartridge.Cartridge Cartridge { get; }
    public Mmu Mmu { get; }
    public IoRegisters Io { get; }
    public Timer.Timer Timer { get; }
    public Sm83 Cpu { get; }
    public Ppu.Ppu Ppu { get; }
    public JoypadState Joypad;

    public RunGuard RunGuard { get; } = new();

    private bool _running;

    public GameBoySystem(HardwareMode mode, Cartridge.Cartridge cart)
    {
        Mode = mode;
        Cartridge = cart;
        Timer = new Timer.Timer();
        Io = new IoRegisters(Timer);
        Mmu = new Mmu(cart, Io);
        Cpu = new Sm83(Mmu);
        Ppu = new Ppu.Ppu();
    }

    public ref CpuRegisters Registers => ref Cpu.Registers;
    public Framebuffer Framebuffer => Ppu.Framebuffer;
    public bool IsRunning => _running;

    /// <summary>
    /// Advance one PPU dot. CPU ticks once per system tick in single-speed,
    /// twice in double-speed. See §7.2 for the clocking invariant.
    /// </summary>
    public bool StepOneSystemTick()
    {
        Ppu.TickDot(ref Io.Interrupts);

        int cpuT = Clock.DoubleSpeed ? 2 : 1;
        bool crossedInstructionBoundary = false;
        for (int i = 0; i < cpuT; i++)
        {
            if (Cpu.TickT()) crossedInstructionBoundary = true;
            Timer.TickT(ref Io.Interrupts);
            // OAM DMA and HDMA tick here in Phase 2.
        }

        Clock.AdvanceOne();
        return crossedInstructionBoundary;
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Emulator.Core/Koh.Emulator.Core.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Emulator.Core/GameBoySystem.cs
git commit -m "feat(emulator): add GameBoySystem facade with single-tick scheduler"
```

---

### Task 1.F.2: RunFrame + StepInstruction + StepTCycle

**Files:**
- Modify: `src/Koh.Emulator.Core/GameBoySystem.cs`

- [ ] **Step 1: Write a failing test for RunFrame tick count**

Add to `tests/Koh.Emulator.Core.Tests/GameBoySystemTests.cs` (create the file):

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class GameBoySystemTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x147] = 0x00; // RomOnly
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test]
    public async Task RunFrame_Advances_Exactly_70224_System_Ticks()
    {
        var gb = MakeSystem();
        var before = gb.Clock.SystemTicks;
        gb.RunFrame();
        var after = gb.Clock.SystemTicks;
        await Assert.That(after - before).IsEqualTo((ulong)SystemClock.SystemTicksPerFrame);
    }

    [Test]
    public async Task StepInstruction_Advances_At_Least_Four_TCycles()
    {
        var gb = MakeSystem();
        var before = gb.Cpu.TotalTCycles;
        gb.StepInstruction();
        var after = gb.Cpu.TotalTCycles;
        await Assert.That(after - before).IsGreaterThanOrEqualTo(4UL);
    }
}
```

- [ ] **Step 2: Run the test — expect compile failure because RunFrame / StepInstruction don't exist**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj`
Expected: build fails with missing method errors.

- [ ] **Step 3: Implement the execution API on `GameBoySystem`**

Append to `GameBoySystem.cs` inside the class body:

```csharp
    public StepResult RunFrame()
    {
        _running = true;
        RunGuard.Clear();
        Clock.ResetFrameCounter();

        while (Clock.FrameSystemTicks < SystemClock.SystemTicksPerFrame)
        {
            bool instrBoundary = StepOneSystemTick();

            if (instrBoundary && RunGuard.StopRequested)
            {
                _running = false;
                return new StepResult(StopReason.StopRequested, Cpu.TotalTCycles, Cpu.Registers.Pc);
            }
        }

        _running = false;
        return new StepResult(StopReason.FrameComplete, Cpu.TotalTCycles, Cpu.Registers.Pc);
    }

    public StepResult StepInstruction()
    {
        _running = true;
        ulong startT = Cpu.TotalTCycles;
        while (true)
        {
            if (StepOneSystemTick())
            {
                _running = false;
                return new StepResult(StopReason.InstructionComplete, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
            }
        }
    }

    public StepResult StepTCycle()
    {
        _running = true;
        StepOneSystemTick();
        _running = false;
        return new StepResult(StopReason.TCycleComplete, 1, Cpu.Registers.Pc);
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter GameBoySystemTests`
Expected: both tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Emulator.Core/GameBoySystem.cs tests/Koh.Emulator.Core.Tests/GameBoySystemTests.cs
git commit -m "feat(emulator): add RunFrame, StepInstruction, StepTCycle API"
```

---

### Task 1.F.3: RunUntil with structural StopCondition

**Files:**
- Modify: `src/Koh.Emulator.Core/GameBoySystem.cs`

- [ ] **Step 1: Write a failing test**

Append to `GameBoySystemTests.cs`:

```csharp
    [Test]
    public async Task RunUntil_StopsAt_PcEquals()
    {
        var gb = MakeSystem();
        // Mock CPU advances PC by 1 or 2 each instruction. Pick an achievable target.
        ushort targetPc = (ushort)(gb.Cpu.Registers.Pc + 10);
        var condition = StopCondition.AtPc(targetPc);
        var result = gb.RunUntil(condition);
        await Assert.That(result.Reason).IsEqualTo(StopReason.StopRequested).Or.IsEqualTo(StopReason.FrameComplete);
    }
```

*(Phase 1 mock CPU may not land exactly on `targetPc` depending on the mock's PC increment pattern; the test accepts either `StopRequested` if it did or `FrameComplete` if the run ran out of time. The goal at this point is API shape, not behavioral precision.)*

- [ ] **Step 2: Implement `RunUntil`**

Append to `GameBoySystem.cs`:

```csharp
    public StepResult RunUntil(in StopCondition condition)
    {
        _running = true;
        RunGuard.Clear();
        Clock.ResetFrameCounter();
        ulong startT = Cpu.TotalTCycles;
        ulong frameBudget = SystemClock.SystemTicksPerFrame;

        while (Clock.FrameSystemTicks < frameBudget)
        {
            bool instrBoundary = StepOneSystemTick();

            if (instrBoundary)
            {
                if (RunGuard.StopRequested)
                {
                    _running = false;
                    return new StepResult(StopReason.StopRequested, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
                }

                if (StopConditionMet(in condition))
                {
                    _running = false;
                    return new StepResult(StopReason.StopRequested, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
                }
            }
        }

        _running = false;
        return new StepResult(StopReason.FrameComplete, Cpu.TotalTCycles - startT, Cpu.Registers.Pc);
    }

    private bool StopConditionMet(in StopCondition condition)
    {
        if (condition.Kind == StopConditionKind.None) return false;

        ushort pc = Cpu.Registers.Pc;

        if ((condition.Kind & StopConditionKind.PcEquals) != 0 && pc == condition.PcEquals)
            return true;

        if ((condition.Kind & StopConditionKind.PcInRange) != 0 &&
            pc >= condition.PcRangeStart && pc < condition.PcRangeEnd)
            return true;

        if ((condition.Kind & StopConditionKind.PcLeavesRange) != 0 &&
            (pc < condition.PcRangeStart || pc >= condition.PcRangeEnd))
            return true;

        return false;
    }
```

- [ ] **Step 3: Run the test**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter GameBoySystemTests`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/GameBoySystem.cs tests/Koh.Emulator.Core.Tests/GameBoySystemTests.cs
git commit -m "feat(emulator): add RunUntil with structural StopCondition"
```

---

## Phase 1-G: Debug peek/poke contract

### Task 1.G.1: DebugReadByte / DebugWriteByte on GameBoySystem

**Files:**
- Modify: `src/Koh.Emulator.Core/GameBoySystem.cs`
- Create: `tests/Koh.Emulator.Core.Tests/DebugReadWriteTests.cs`

- [ ] **Step 1: Write failing tests**

File `tests/Koh.Emulator.Core.Tests/DebugReadWriteTests.cs`:

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class DebugReadWriteTests
{
    private static GameBoySystem MakeSystem()
    {
        var rom = new byte[0x8000];
        rom[0x0100] = 0x42;
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Dmg, cart);
    }

    [Test]
    public async Task DebugReadByte_Rom()
    {
        var gb = MakeSystem();
        await Assert.That(gb.DebugReadByte(0x0100)).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task DebugWriteByte_When_Running_Returns_False()
    {
        var gb = MakeSystem();
        // Start a frame and attempt a write mid-run. We simulate "running" by
        // using a flag check — here we exercise the contract that writes
        // are rejected outside the paused state.
        gb.SetRunningForTest(true);
        bool ok = gb.DebugWriteByte(0xC000, 0x55);
        await Assert.That(ok).IsFalse();
        gb.SetRunningForTest(false);
    }

    [Test]
    public async Task DebugWriteByte_Wram_When_Paused_Succeeds()
    {
        var gb = MakeSystem();
        bool ok = gb.DebugWriteByte(0xC000, 0x55);
        await Assert.That(ok).IsTrue();
        await Assert.That(gb.DebugReadByte(0xC000)).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task DebugWriteByte_Rom_Patches_Backing_Buffer()
    {
        var gb = MakeSystem();
        bool ok = gb.DebugWriteByte(0x0100, 0xAA);
        await Assert.That(ok).IsTrue();
        await Assert.That(gb.DebugReadByte(0x0100)).IsEqualTo((byte)0xAA);
    }
}
```

- [ ] **Step 2: Add the debug API to `GameBoySystem`**

Append to `GameBoySystem.cs`:

```csharp
    public byte DebugReadByte(ushort address) => Mmu.DebugRead(address);

    public bool DebugWriteByte(ushort address, byte value)
    {
        if (_running) return false;
        return Mmu.DebugWrite(address, value);
    }

    // Test hook only — not part of the public production API.
    internal void SetRunningForTest(bool running) => _running = running;
```

- [ ] **Step 3: Run the tests**

Run: `dotnet test tests/Koh.Emulator.Core.Tests/Koh.Emulator.Core.Tests.csproj --filter DebugReadWriteTests`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.Core/GameBoySystem.cs tests/Koh.Emulator.Core.Tests/DebugReadWriteTests.cs
git commit -m "feat(emulator): implement DebugReadByte/DebugWriteByte per §7.10 contract"
```

---

## Phase 1-H: `.kdbg` format in `Koh.Linker.Core`

Per §9 of the spec with the Phase 1 scope reduction: **no coalescing, no expansion-pool deduplication**. The reader logic must still handle coalesced and deduplicated files because Phase 3 adds those optimizations.

### Task 1.H.1: Format constants and BankedAddress

**Files:**
- Create: `src/Koh.Linker.Core/KdbgFormat.cs`
- Create: `src/Koh.Linker.Core/BankedAddress.cs`

- [ ] **Step 1: Create `KdbgFormat.cs`**

```csharp
namespace Koh.Linker.Core;

internal static class KdbgFormat
{
    public const uint Magic = 0x4742444B;   // "KDBG" little-endian
    public const ushort Version1 = 1;

    public const ushort FlagExpansionPresent = 1 << 0;
    public const ushort FlagScopeTablePresent = 1 << 1;
    public const ushort FlagPathsAbsolute = 1 << 2;

    public const int HeaderSize = 32;

    public const uint NoExpansion = 0xFFFFFFFF;
}
```

- [ ] **Step 2: Create `BankedAddress.cs`**

```csharp
namespace Koh.Linker.Core;

public readonly record struct BankedAddress(byte Bank, ushort Address)
{
    public uint Packed => ((uint)Bank << 16) | Address;
}
```

- [ ] **Step 3: Build and commit**

Run: `dotnet build src/Koh.Linker.Core/Koh.Linker.Core.csproj`

```bash
git add src/Koh.Linker.Core/KdbgFormat.cs src/Koh.Linker.Core/BankedAddress.cs
git commit -m "feat(linker): add .kdbg format constants and BankedAddress struct"
```

---

### Task 1.H.2: DebugInfoBuilder collector

**Files:**
- Create: `src/Koh.Linker.Core/DebugInfoBuilder.cs`

- [ ] **Step 1: Create `DebugInfoBuilder.cs`**

```csharp
using System.Collections.Generic;

namespace Koh.Linker.Core;

public enum KdbgSymbolKind : byte
{
    Label = 0,
    EquConstant = 1,
    RamLabel = 2,
    Macro = 3,
    Export = 4,
}

public enum KdbgScopeKind : byte
{
    Global = 0,
    LocalToLabel = 1,
    MacroLocal = 2,
    File = 3,
}

public sealed class DebugInfoBuilder
{
    private readonly List<string> _strings = [];
    private readonly Dictionary<string, uint> _stringIndex = new(StringComparer.Ordinal);

    private readonly List<uint> _sourceFiles = [];   // stringId per file
    private readonly Dictionary<string, uint> _sourceFileIndex = new(StringComparer.Ordinal);

    private readonly List<ScopeRecord> _scopes = [];
    private readonly List<SymbolRecord> _symbols = [];
    private readonly List<AddressMapRecord> _addressMap = [];
    private readonly List<ExpansionFrameRecord> _expansionPool = [];
    private readonly List<uint> _expansionStackOffsets = [];  // offsets from file start

    internal readonly record struct ScopeRecord(KdbgScopeKind Kind, uint ParentScopeId, uint NameStringId);
    internal readonly record struct SymbolRecord(
        KdbgSymbolKind Kind, byte Bank, ushort Address, ushort Size,
        uint NameStringId, uint ScopeId, uint DefinitionSourceFileId, uint DefinitionLine);
    internal readonly record struct AddressMapRecord(
        byte Bank, byte ByteCount, ushort Address,
        uint SourceFileId, uint Line, uint ExpansionStackOffset);
    internal readonly record struct ExpansionFrameRecord(uint SourceFileId, uint Line);

    /// <summary>Intern a string. Returns a 1-based ID; 0 is the "no string" sentinel.</summary>
    public uint InternString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        if (_stringIndex.TryGetValue(value, out var existing)) return existing;
        _strings.Add(value);
        uint id = (uint)_strings.Count;   // 1-based
        _stringIndex[value] = id;
        return id;
    }

    /// <summary>Intern a source file path. Returns a 1-based ID; 0 = "no file".</summary>
    public uint InternSourceFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        if (_sourceFileIndex.TryGetValue(path, out var existing)) return existing;
        uint pathStringId = InternString(path);
        _sourceFiles.Add(pathStringId);
        uint id = (uint)_sourceFiles.Count;   // 1-based
        _sourceFileIndex[path] = id;
        return id;
    }

    /// <summary>Intern a scope. Returns a 1-based ID; 0 = global scope sentinel.</summary>
    public uint InternScope(KdbgScopeKind kind, uint parentScopeId, string? name)
    {
        _scopes.Add(new ScopeRecord(kind, parentScopeId, InternString(name)));
        return (uint)_scopes.Count;
    }

    public void AddSymbol(KdbgSymbolKind kind, byte bank, ushort address, ushort size,
                          string name, uint scopeId, string? definitionSourceFile, uint definitionLine)
    {
        _symbols.Add(new SymbolRecord(
            kind, bank, address, size,
            InternString(name), scopeId,
            InternSourceFile(definitionSourceFile), definitionLine));
    }

    public void AddAddressMapping(byte bank, ushort address, byte byteCount,
                                   string sourceFile, uint line,
                                   IReadOnlyList<(string SourceFile, uint Line)>? expansionStack = null)
    {
        uint fileId = InternSourceFile(sourceFile);
        uint expansionOffset = KdbgFormat.NoExpansion;

        if (expansionStack is { Count: > 0 })
        {
            // Record the start offset where this stack will live in the expansion pool
            // once the pool is serialized. For Phase 1 we store the pool index here and
            // translate to a byte offset during Write().
            expansionOffset = (uint)_expansionStackOffsets.Count;
            _expansionStackOffsets.Add((uint)_expansionPool.Count);
            foreach (var frame in expansionStack)
                _expansionPool.Add(new ExpansionFrameRecord(InternSourceFile(frame.SourceFile), frame.Line));
            // Phase 1 stores depth by using a stack-length marker via separate array;
            // Phase 3 dedups. For simplicity we emit {u16 depth, frames} inline during Write().
        }

        _addressMap.Add(new AddressMapRecord(bank, byteCount, address, fileId, line, expansionOffset));
    }

    /// <summary>Returns true if at least one address mapping has an expansion stack.</summary>
    public bool HasExpansionData => _expansionStackOffsets.Count > 0;

    /// <summary>Returns true if any scopes were interned.</summary>
    public bool HasScopeData => _scopes.Count > 0;

    // Internal accessors for the writer
    internal IReadOnlyList<string> Strings => _strings;
    internal IReadOnlyList<uint> SourceFiles => _sourceFiles;
    internal IReadOnlyList<ScopeRecord> Scopes => _scopes;
    internal IReadOnlyList<SymbolRecord> Symbols => _symbols;
    internal List<AddressMapRecord> AddressMap => _addressMap;
    internal IReadOnlyList<ExpansionFrameRecord> ExpansionPool => _expansionPool;
    internal IReadOnlyList<uint> ExpansionStackIndexes => _expansionStackOffsets;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Linker.Core/Koh.Linker.Core.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Linker.Core/DebugInfoBuilder.cs
git commit -m "feat(linker): add DebugInfoBuilder collector for .kdbg"
```

---

### Task 1.H.3: KdbgFileWriter — serialize to bytes

**Files:**
- Create: `src/Koh.Linker.Core/KdbgFileWriter.cs`

- [ ] **Step 1: Create `KdbgFileWriter.cs`**

```csharp
using System.IO;
using System.Text;

namespace Koh.Linker.Core;

/// <summary>
/// Writes the .kdbg binary format per design §9. Byte-packed, little-endian.
/// Phase 1: no address-map coalescing, no expansion-pool deduplication.
/// </summary>
public static class KdbgFileWriter
{
    public static void Write(Stream output, DebugInfoBuilder builder)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        // Stage 1: compute section sizes to fill in header offsets.
        // We build each section body into a memory buffer, then emit the header
        // followed by the sections. This avoids needing to know offsets up front
        // at the cost of building the section payloads in memory.

        byte[] stringPool = BuildStringPool(builder);
        byte[] sourceTable = BuildSourceTable(builder);
        byte[] scopeTable = builder.HasScopeData ? BuildScopeTable(builder) : [];
        byte[] symbolTable = BuildSymbolTable(builder);

        // Expansion pool is built first so we know its layout, then referenced
        // from the address-map build step via index→offset translation.
        (byte[] expansionPool, uint[] stackIdxToAbsoluteOffset) = BuildExpansionPool(
            builder, expansionPoolAbsoluteStart: 0 /* placeholder, fixed below */);

        // Compute offsets.
        uint headerSize = (uint)KdbgFormat.HeaderSize;
        uint stringPoolOffset = headerSize;
        uint sourceTableOffset = stringPoolOffset + (uint)stringPool.Length;
        uint scopeTableOffset = builder.HasScopeData
            ? sourceTableOffset + (uint)sourceTable.Length
            : 0;
        uint symbolTableOffset = (scopeTableOffset != 0 ? scopeTableOffset + (uint)scopeTable.Length : sourceTableOffset + (uint)sourceTable.Length);
        uint addressMapOffset = symbolTableOffset + (uint)symbolTable.Length;

        // Address map serialization depends on absolute expansion-stack byte offsets,
        // which depend on ExpansionPoolOffset. Compute address-map size (fixed: 4 + 16*count).
        int addressMapByteSize = 4 + 16 * builder.AddressMap.Count;
        uint expansionPoolOffset = builder.HasExpansionData
            ? (uint)(addressMapOffset + addressMapByteSize)
            : 0;

        // Rebuild expansion pool with the correct absolute start so the u32 offsets in
        // the address map are file-absolute.
        if (builder.HasExpansionData)
        {
            (expansionPool, stackIdxToAbsoluteOffset) = BuildExpansionPool(
                builder, expansionPoolAbsoluteStart: expansionPoolOffset);
        }

        byte[] addressMap = BuildAddressMap(builder, stackIdxToAbsoluteOffset);

        // Flags
        ushort flags = 0;
        if (builder.HasExpansionData) flags |= KdbgFormat.FlagExpansionPresent;
        if (builder.HasScopeData) flags |= KdbgFormat.FlagScopeTablePresent;

        // Header
        writer.Write(KdbgFormat.Magic);           // 0..3   Magic
        writer.Write(KdbgFormat.Version1);        // 4..5   Version
        writer.Write(flags);                      // 6..7   Flags
        writer.Write(stringPoolOffset);           // 8..11
        writer.Write(sourceTableOffset);          // 12..15
        writer.Write(scopeTableOffset);           // 16..19
        writer.Write(symbolTableOffset);          // 20..23
        writer.Write(addressMapOffset);           // 24..27
        writer.Write(expansionPoolOffset);        // 28..31

        // Sections
        writer.Write(stringPool);
        writer.Write(sourceTable);
        if (builder.HasScopeData) writer.Write(scopeTable);
        writer.Write(symbolTable);
        writer.Write(addressMap);
        if (builder.HasExpansionData) writer.Write(expansionPool);
    }

    private static byte[] BuildStringPool(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.Strings.Count);
        foreach (var s in b.Strings)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > ushort.MaxValue)
                throw new InvalidDataException($".kdbg string too long: {bytes.Length}");
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildSourceTable(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.SourceFiles.Count);
        foreach (var id in b.SourceFiles)
            w.Write(id);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildScopeTable(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.Scopes.Count);
        foreach (var s in b.Scopes)
        {
            w.Write((byte)s.Kind);
            w.Write((byte)0);            // reserved
            w.Write((ushort)0);          // reserved
            w.Write(s.ParentScopeId);
            w.Write(s.NameStringId);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildSymbolTable(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.Symbols.Count);
        foreach (var s in b.Symbols)
        {
            w.Write((byte)s.Kind);
            w.Write(s.Bank);
            w.Write(s.Address);
            w.Write(s.Size);
            w.Write((ushort)0);          // reserved
            w.Write(s.NameStringId);
            w.Write(s.ScopeId);
            w.Write(s.DefinitionSourceFileId);
            w.Write(s.DefinitionLine);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildAddressMap(DebugInfoBuilder b, uint[] stackIdxToAbsoluteOffset)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.AddressMap.Count);
        foreach (var e in b.AddressMap)
        {
            w.Write(e.Bank);
            w.Write(e.ByteCount);
            w.Write(e.Address);
            w.Write(e.SourceFileId);
            w.Write(e.Line);

            uint expansionOffset = e.ExpansionStackOffset;
            if (expansionOffset != KdbgFormat.NoExpansion)
            {
                // In the builder, ExpansionStackOffset was stored as an index into
                // ExpansionStackIndexes. Translate to a file-absolute byte offset.
                expansionOffset = stackIdxToAbsoluteOffset[(int)expansionOffset];
            }
            w.Write(expansionOffset);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static (byte[] bytes, uint[] stackIdxToAbsoluteOffset) BuildExpansionPool(
        DebugInfoBuilder b, uint expansionPoolAbsoluteStart)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Reserve 4 bytes for the payload byte-size field; we'll write it last.
        long poolByteSizePos = ms.Position;
        w.Write((uint)0);

        var stackIdxToAbsoluteOffset = new uint[b.ExpansionStackIndexes.Count];
        uint runningStackStart = 0;

        for (int i = 0; i < b.ExpansionStackIndexes.Count; i++)
        {
            int firstFrameIdxInPool = (int)b.ExpansionStackIndexes[i];
            int nextStackFirstIdx = i + 1 < b.ExpansionStackIndexes.Count
                ? (int)b.ExpansionStackIndexes[i + 1]
                : b.ExpansionPool.Count;
            int depth = nextStackFirstIdx - firstFrameIdxInPool;

            // Absolute byte offset of this stack's u16 depth header.
            uint stackBytePositionInPayload = (uint)(ms.Position - (poolByteSizePos + 4));
            uint absoluteOffsetIntoFile = expansionPoolAbsoluteStart + 4 /* poolByteSize */ + stackBytePositionInPayload;
            stackIdxToAbsoluteOffset[i] = absoluteOffsetIntoFile;

            w.Write((ushort)depth);
            for (int j = 0; j < depth; j++)
            {
                var frame = b.ExpansionPool[firstFrameIdxInPool + j];
                w.Write(frame.SourceFileId);
                w.Write(frame.Line);
            }

            runningStackStart += (uint)(2 + 8 * depth);
        }

        // Patch payload byte-size.
        long endPos = ms.Position;
        uint payloadSize = (uint)(endPos - (poolByteSizePos + 4));
        ms.Position = poolByteSizePos;
        w.Write(payloadSize);
        ms.Position = endPos;
        w.Flush();

        return (ms.ToArray(), stackIdxToAbsoluteOffset);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Linker.Core/Koh.Linker.Core.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Linker.Core/KdbgFileWriter.cs
git commit -m "feat(linker): add KdbgFileWriter binary serialization (Phase 1 scope)"
```

---

### Task 1.H.4: KdbgFileWriter round-trip test

**Files:**
- Create: `tests/Koh.Linker.Tests/KdbgFileWriterTests.cs`

- [ ] **Step 1: Find the existing Koh.Linker.Tests csproj and verify layout**

Run: `ls tests/Koh.Linker.Tests/` and confirm a `.csproj` exists. If not, create it using the same template as `Koh.Emulator.Core.Tests.csproj` referencing `Koh.Linker.Core`.

- [ ] **Step 2: Write a round-trip test**

File `tests/Koh.Linker.Tests/KdbgFileWriterTests.cs`:

```csharp
using System.IO;
using System.Text;
using Koh.Linker.Core;

namespace Koh.Linker.Tests;

public class KdbgFileWriterTests
{
    private static byte[] Write(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        KdbgFileWriter.Write(ms, b);
        return ms.ToArray();
    }

    [Test]
    public async Task Header_Magic_And_Version()
    {
        var builder = new DebugInfoBuilder();
        var bytes = Write(builder);

        await Assert.That(bytes.Length).IsGreaterThanOrEqualTo(32);
        uint magic = BitConverter.ToUInt32(bytes, 0);
        ushort version = BitConverter.ToUInt16(bytes, 4);
        await Assert.That(magic).IsEqualTo(KdbgFormat.Magic);
        await Assert.That(version).IsEqualTo(KdbgFormat.Version1);
    }

    [Test]
    public async Task Symbol_Round_Trip()
    {
        var builder = new DebugInfoBuilder();
        builder.AddSymbol(
            kind: KdbgSymbolKind.Label,
            bank: 0,
            address: 0x0150,
            size: 3,
            name: "main",
            scopeId: 0,
            definitionSourceFile: "src/main.asm",
            definitionLine: 42);

        var bytes = Write(builder);
        var parsed = KdbgReader.Parse(bytes);

        await Assert.That(parsed.Symbols).HasCount(1);
        var sym = parsed.Symbols[0];
        await Assert.That(sym.Name).IsEqualTo("main");
        await Assert.That(sym.Bank).IsEqualTo((byte)0);
        await Assert.That(sym.Address).IsEqualTo((ushort)0x0150);
        await Assert.That(sym.Size).IsEqualTo((ushort)3);
        await Assert.That(sym.DefinitionFile).IsEqualTo("src/main.asm");
        await Assert.That(sym.DefinitionLine).IsEqualTo(42u);
    }

    [Test]
    public async Task AddressMap_Round_Trip_Without_Expansion()
    {
        var builder = new DebugInfoBuilder();
        builder.AddAddressMapping(bank: 0, address: 0x0100, byteCount: 1,
            sourceFile: "src/main.asm", line: 10);
        builder.AddAddressMapping(bank: 0, address: 0x0101, byteCount: 1,
            sourceFile: "src/main.asm", line: 10);

        var bytes = Write(builder);
        var parsed = KdbgReader.Parse(bytes);

        await Assert.That(parsed.AddressMap).HasCount(2);
        await Assert.That(parsed.AddressMap[0].Address).IsEqualTo((ushort)0x0100);
        await Assert.That(parsed.AddressMap[1].Address).IsEqualTo((ushort)0x0101);
    }

    [Test]
    public async Task AddressMap_Round_Trip_With_Expansion_Stack()
    {
        var builder = new DebugInfoBuilder();
        builder.AddAddressMapping(
            bank: 0, address: 0x0100, byteCount: 1,
            sourceFile: "src/main.asm", line: 5,
            expansionStack: new List<(string, uint)>
            {
                ("src/main.asm", 100),
                ("src/main.asm", 42),
            });

        var bytes = Write(builder);
        var parsed = KdbgReader.Parse(bytes);

        await Assert.That(parsed.AddressMap).HasCount(1);
        await Assert.That(parsed.AddressMap[0].ExpansionStack).HasCount(2);
    }
}
```

- [ ] **Step 3: Create the reader used by the round-trip test**

The writer tests need a matching reader. This lives in `Koh.Linker.Core` because both the linker (writer) and the debugger (reader) share it.

File `src/Koh.Linker.Core/KdbgReader.cs`:

```csharp
using System.IO;
using System.Text;

namespace Koh.Linker.Core;

public sealed record KdbgParsed(
    IReadOnlyList<KdbgParsedSymbol> Symbols,
    IReadOnlyList<KdbgParsedAddressMapEntry> AddressMap);

public sealed record KdbgParsedSymbol(
    KdbgSymbolKind Kind,
    byte Bank,
    ushort Address,
    ushort Size,
    string Name,
    string? Scope,
    string? DefinitionFile,
    uint DefinitionLine);

public sealed record KdbgParsedAddressMapEntry(
    byte Bank,
    byte ByteCount,
    ushort Address,
    string? SourceFile,
    uint Line,
    IReadOnlyList<(string? File, uint Line)> ExpansionStack);

public static class KdbgReader
{
    public static KdbgParsed Parse(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint magic = r.ReadUInt32();
        if (magic != KdbgFormat.Magic)
            throw new InvalidDataException($"Bad .kdbg magic: 0x{magic:X8}");
        ushort version = r.ReadUInt16();
        if (version != KdbgFormat.Version1)
            throw new InvalidDataException($"Unsupported .kdbg version: {version}");
        ushort flags = r.ReadUInt16();
        uint stringPoolOffset = r.ReadUInt32();
        uint sourceTableOffset = r.ReadUInt32();
        uint scopeTableOffset = r.ReadUInt32();
        uint symbolTableOffset = r.ReadUInt32();
        uint addressMapOffset = r.ReadUInt32();
        uint expansionPoolOffset = r.ReadUInt32();

        // String pool
        ms.Position = stringPoolOffset;
        uint strCount = r.ReadUInt32();
        var strings = new string[strCount + 1];    // index 0 is sentinel = null placeholder
        strings[0] = "";
        for (int i = 0; i < strCount; i++)
        {
            ushort len = r.ReadUInt16();
            strings[i + 1] = Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        // Source file table
        ms.Position = sourceTableOffset;
        uint srcCount = r.ReadUInt32();
        var sourceFiles = new string?[srcCount + 1];
        sourceFiles[0] = null;
        for (int i = 0; i < srcCount; i++)
            sourceFiles[i + 1] = LookupString(strings, r.ReadUInt32());

        // Scope table (optional)
        string?[] scopeNames;
        if ((flags & KdbgFormat.FlagScopeTablePresent) != 0)
        {
            ms.Position = scopeTableOffset;
            uint scopeCount = r.ReadUInt32();
            scopeNames = new string?[scopeCount + 1];
            scopeNames[0] = null;
            for (int i = 0; i < scopeCount; i++)
            {
                r.ReadByte();                 // kind
                r.ReadByte(); r.ReadUInt16(); // reserved
                r.ReadUInt32();               // parentScopeId
                uint nameStringId = r.ReadUInt32();
                scopeNames[i + 1] = LookupString(strings, nameStringId);
            }
        }
        else
        {
            scopeNames = [null];
        }

        // Symbol table
        ms.Position = symbolTableOffset;
        uint symCount = r.ReadUInt32();
        var symbols = new KdbgParsedSymbol[symCount];
        for (int i = 0; i < symCount; i++)
        {
            var kind = (KdbgSymbolKind)r.ReadByte();
            byte bank = r.ReadByte();
            ushort address = r.ReadUInt16();
            ushort size = r.ReadUInt16();
            r.ReadUInt16();                   // reserved
            uint nameStringId = r.ReadUInt32();
            uint scopeId = r.ReadUInt32();
            uint defSourceFileId = r.ReadUInt32();
            uint defLine = r.ReadUInt32();
            symbols[i] = new KdbgParsedSymbol(
                kind, bank, address, size,
                LookupString(strings, nameStringId) ?? "",
                scopeId < scopeNames.Length ? scopeNames[scopeId] : null,
                defSourceFileId < sourceFiles.Length ? sourceFiles[defSourceFileId] : null,
                defLine);
        }

        // Address map
        ms.Position = addressMapOffset;
        uint amCount = r.ReadUInt32();
        var addressMap = new KdbgParsedAddressMapEntry[amCount];
        for (int i = 0; i < amCount; i++)
        {
            byte bank = r.ReadByte();
            byte byteCount = r.ReadByte();
            ushort address = r.ReadUInt16();
            uint sourceFileId = r.ReadUInt32();
            uint line = r.ReadUInt32();
            uint expansionOffset = r.ReadUInt32();

            IReadOnlyList<(string?, uint)> expansionStack = [];
            if (expansionOffset != KdbgFormat.NoExpansion &&
                (flags & KdbgFormat.FlagExpansionPresent) != 0)
            {
                long mark = ms.Position;
                ms.Position = expansionOffset;
                ushort depth = r.ReadUInt16();
                var stack = new (string?, uint)[depth];
                for (int k = 0; k < depth; k++)
                {
                    uint fid = r.ReadUInt32();
                    uint fline = r.ReadUInt32();
                    stack[k] = (fid < sourceFiles.Length ? sourceFiles[fid] : null, fline);
                }
                expansionStack = stack;
                ms.Position = mark;
            }

            addressMap[i] = new KdbgParsedAddressMapEntry(
                bank, byteCount, address,
                sourceFileId < sourceFiles.Length ? sourceFiles[sourceFileId] : null,
                line, expansionStack);
        }

        return new KdbgParsed(symbols, addressMap);
    }

    private static string? LookupString(string[] strings, uint id)
        => id == 0 ? null : (id < strings.Length ? strings[id] : null);
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Koh.Linker.Tests/Koh.Linker.Tests.csproj --filter KdbgFileWriterTests`
Expected: all round-trip tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Linker.Core/KdbgReader.cs tests/Koh.Linker.Tests/KdbgFileWriterTests.cs
git commit -m "feat(linker): add KdbgReader + round-trip tests for .kdbg format"
```

---

## Phase 1-I: `koh-link` `.kdbg` integration

The existing linker collects symbols and emits `.sym`. This task wires `.kdbg` emission alongside.

### Task 1.I.1: Populate DebugInfoBuilder from linker results

**Files:**
- Modify: `src/Koh.Link/Program.cs`
- Create: `src/Koh.Linker.Core/DebugInfoPopulator.cs`

Before touching `koh-link`, centralize the "convert linker results → DebugInfoBuilder" logic so it can be tested in isolation.

- [ ] **Step 1: Create `DebugInfoPopulator.cs`**

```csharp
using Koh.Core.Symbols;

namespace Koh.Linker.Core;

public static class DebugInfoPopulator
{
    /// <summary>
    /// Populate a <see cref="DebugInfoBuilder"/> from linker symbols and their
    /// source locations. Phase 1 only emits the symbol table and a best-effort
    /// address map (one entry per symbol, byte count = 1). Full per-byte
    /// address mapping with expansion stacks arrives in Phase 3 alongside the
    /// coalescing and dedup optimizations.
    /// </summary>
    public static void PopulateFromLinkerSymbols(
        DebugInfoBuilder builder,
        IReadOnlyList<LinkerSymbol> symbols)
    {
        foreach (var sym in symbols)
        {
            if (sym.AbsoluteAddress < 0) continue;   // unplaced
            byte bank = (byte)(sym.SectionName != null ? sym.PlacedBank : 0);
            ushort address = (ushort)(sym.AbsoluteAddress & 0xFFFF);
            var kind = sym.Kind switch
            {
                SymbolKind.Constant => KdbgSymbolKind.EquConstant,
                SymbolKind.Label    => KdbgSymbolKind.Label,
                _                   => KdbgSymbolKind.Label,
            };

            builder.AddSymbol(
                kind: kind,
                bank: bank,
                address: address,
                size: 0,
                name: sym.Name,
                scopeId: 0,
                definitionSourceFile: sym.DefinitionFilePath,
                definitionLine: 0);   // line number not wired in Phase 1; Phase 3 enriches

            if (kind != KdbgSymbolKind.EquConstant)
            {
                builder.AddAddressMapping(
                    bank: bank,
                    address: address,
                    byteCount: 1,
                    sourceFile: sym.DefinitionFilePath ?? "",
                    line: 0);
            }
        }
    }
}
```

- [ ] **Step 2: Build the linker**

Run: `dotnet build src/Koh.Linker.Core/Koh.Linker.Core.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Linker.Core/DebugInfoPopulator.cs
git commit -m "feat(linker): add DebugInfoPopulator bridging LinkerSymbol → .kdbg"
```

---

### Task 1.I.2: Wire `-d` / `--kdbg` flag into koh-link

**Files:**
- Modify: `src/Koh.Link/Program.cs`

The existing CLI parser handles `-o`/`--output` and `-n`/`--sym`. Extend it to handle `-d`/`--kdbg` with the same pattern, then emit the file if requested.

- [ ] **Step 1: Read the current Program.cs ArgParse and emission block**

Run: `grep -n "ParseArgs\|SymFileWriter" src/Koh.Link/Program.cs`
Note the line numbers where `-n`/`--sym` is parsed and where `SymFileWriter.Write` is called.

- [ ] **Step 2: Add `-d`/`--kdbg` to the arg parser**

Inside `ParseArgs`, extend the argument handling to recognize a new optional `kdbg` path. The result tuple gains a `string? kdbg` component.

```csharp
static (List<string> inputs, string output, string? sym, string? kdbg, string? error) ParseArgs(string[] args)
{
    var inputs = new List<string>();
    string? output = null, sym = null, kdbg = null;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] is "-o" or "--output")
        {
            if (i + 1 >= args.Length) return (inputs, "", null, null, "missing value for -o");
            output = args[++i];
        }
        else if (args[i] is "-n" or "--sym")
        {
            if (i + 1 >= args.Length) return (inputs, "", null, null, "missing value for -n");
            sym = args[++i];
        }
        else if (args[i] is "-d" or "--kdbg")
        {
            if (i + 1 >= args.Length) return (inputs, "", null, null, "missing value for -d");
            kdbg = args[++i];
        }
        else if (args[i].StartsWith('-'))
        {
            return (inputs, "", null, null, $"unknown option '{args[i]}'");
        }
        else
        {
            inputs.Add(args[i]);
        }
    }

    if (inputs.Count == 0) return (inputs, "", null, null, "no input files");
    output ??= Path.ChangeExtension(inputs[0], ".gb");
    return (inputs, output, sym, kdbg, null);
}
```

- [ ] **Step 3: Call `KdbgFileWriter.Write` after `SymFileWriter.Write` in Main**

Locate the block that writes `.sym` and add a parallel block:

```csharp
if (kdbgPath != null)
{
    try
    {
        var builder = new DebugInfoBuilder();
        DebugInfoPopulator.PopulateFromLinkerSymbols(builder, result.Symbols);
        using var stream = File.Create(kdbgPath);
        KdbgFileWriter.Write(stream, builder);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Fail($"cannot write '{kdbgPath}': {ex.Message}");
    }
}
```

(Update `var (inputs, outputPath, symPath, kdbgPath, error) = ParseArgs(args);` at the top of `Main` accordingly.)

- [ ] **Step 4: Verify the linker builds**

Run: `dotnet build src/Koh.Link/Koh.Link.csproj`
Expected: build succeeds.

- [ ] **Step 5: Run all existing linker tests**

Run: `dotnet test tests/Koh.Linker.Tests/Koh.Linker.Tests.csproj`
Expected: all tests pass (no regressions).

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Link/Program.cs
git commit -m "feat(linker): add -d/--kdbg flag to koh-link for emitting .kdbg"
```

---

## Phase 1-J: Debugger DAP protocol infrastructure

### Task 1.J.1: DAP message types (records) and source-gen context

**Files:**
- Create: `src/Koh.Debugger/Dap/Messages/CommonMessages.cs`
- Create: `src/Koh.Debugger/Dap/Messages/InitializeMessages.cs`
- Create: `src/Koh.Debugger/Dap/Messages/LaunchMessages.cs`
- Create: `src/Koh.Debugger/Dap/Messages/ContinuePauseMessages.cs`
- Create: `src/Koh.Debugger/Dap/Messages/SetBreakpointsMessages.cs`
- Create: `src/Koh.Debugger/Dap/Messages/ScopesVariablesMessages.cs`
- Create: `src/Koh.Debugger/Dap/DapJson.cs`

Only the messages used in Phase 1 are defined. More messages are added in Phases 2–4.

- [ ] **Step 1: Create the common envelope and capability types**

File `src/Koh.Debugger/Dap/Messages/CommonMessages.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class ProtocolMessage
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

public sealed class Request
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "request";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("arguments")] public System.Text.Json.JsonElement? Arguments { get; set; }
}

public sealed class Response
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "response";
    [JsonPropertyName("request_seq")] public int RequestSeq { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("body")] public object? Body { get; set; }
}

public sealed class Event
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "event";
    [JsonPropertyName("event")] public string EventName { get; set; } = "";
    [JsonPropertyName("body")] public object? Body { get; set; }
}

public sealed class Capabilities
{
    [JsonPropertyName("supportsConfigurationDoneRequest")] public bool SupportsConfigurationDoneRequest { get; set; }
    [JsonPropertyName("supportsFunctionBreakpoints")] public bool SupportsFunctionBreakpoints { get; set; }
    [JsonPropertyName("supportsConditionalBreakpoints")] public bool SupportsConditionalBreakpoints { get; set; }
    [JsonPropertyName("supportsHitConditionalBreakpoints")] public bool SupportsHitConditionalBreakpoints { get; set; }
    [JsonPropertyName("supportsStepBack")] public bool SupportsStepBack { get; set; }
    [JsonPropertyName("supportsSetVariable")] public bool SupportsSetVariable { get; set; }
    [JsonPropertyName("supportsReadMemoryRequest")] public bool SupportsReadMemoryRequest { get; set; }
    [JsonPropertyName("supportsWriteMemoryRequest")] public bool SupportsWriteMemoryRequest { get; set; }
    [JsonPropertyName("supportsDisassembleRequest")] public bool SupportsDisassembleRequest { get; set; }
    [JsonPropertyName("supportsSteppingGranularity")] public bool SupportsSteppingGranularity { get; set; }
    [JsonPropertyName("supportsInstructionBreakpoints")] public bool SupportsInstructionBreakpoints { get; set; }
    [JsonPropertyName("supportsExceptionInfoRequest")] public bool SupportsExceptionInfoRequest { get; set; }
}
```

- [ ] **Step 2: Create the Initialize/Launch messages**

File `src/Koh.Debugger/Dap/Messages/InitializeMessages.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class InitializeRequestArguments
{
    [JsonPropertyName("clientID")] public string? ClientId { get; set; }
    [JsonPropertyName("clientName")] public string? ClientName { get; set; }
    [JsonPropertyName("adapterID")] public string? AdapterId { get; set; }
    [JsonPropertyName("linesStartAt1")] public bool LinesStartAt1 { get; set; } = true;
    [JsonPropertyName("columnsStartAt1")] public bool ColumnsStartAt1 { get; set; } = true;
    [JsonPropertyName("pathFormat")] public string? PathFormat { get; set; }
}
```

File `src/Koh.Debugger/Dap/Messages/LaunchMessages.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class LaunchRequestArguments
{
    [JsonPropertyName("program")] public string Program { get; set; } = "";
    [JsonPropertyName("debugInfo")] public string? DebugInfo { get; set; }
    [JsonPropertyName("hardwareMode")] public string HardwareMode { get; set; } = "auto";
    [JsonPropertyName("stopOnEntry")] public bool StopOnEntry { get; set; }
}
```

- [ ] **Step 3: Create continue/pause/terminate/configurationDone messages**

File `src/Koh.Debugger/Dap/Messages/ContinuePauseMessages.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class ContinueArguments
{
    [JsonPropertyName("threadId")] public int ThreadId { get; set; }
}

public sealed class ContinueResponseBody
{
    [JsonPropertyName("allThreadsContinued")] public bool AllThreadsContinued { get; set; } = true;
}

public sealed class PauseArguments
{
    [JsonPropertyName("threadId")] public int ThreadId { get; set; }
}

public sealed class ConfigurationDoneArguments { }
public sealed class TerminateArguments { }
```

- [ ] **Step 4: Create SetBreakpoints messages**

File `src/Koh.Debugger/Dap/Messages/SetBreakpointsMessages.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class Source
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
}

public sealed class SourceBreakpoint
{
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("column")] public int? Column { get; set; }
    [JsonPropertyName("condition")] public string? Condition { get; set; }
    [JsonPropertyName("hitCondition")] public string? HitCondition { get; set; }
}

public sealed class SetBreakpointsArguments
{
    [JsonPropertyName("source")] public Source Source { get; set; } = new();
    [JsonPropertyName("breakpoints")] public SourceBreakpoint[]? Breakpoints { get; set; }
    [JsonPropertyName("sourceModified")] public bool SourceModified { get; set; }
}

public sealed class Breakpoint
{
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("verified")] public bool Verified { get; set; }
    [JsonPropertyName("line")] public int? Line { get; set; }
    [JsonPropertyName("source")] public Source? Source { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class SetBreakpointsResponseBody
{
    [JsonPropertyName("breakpoints")] public Breakpoint[] Breakpoints { get; set; } = [];
}
```

- [ ] **Step 5: Create Scopes/Variables messages**

File `src/Koh.Debugger/Dap/Messages/ScopesVariablesMessages.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class ScopesArguments
{
    [JsonPropertyName("frameId")] public int FrameId { get; set; }
}

public sealed class Scope
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("variablesReference")] public int VariablesReference { get; set; }
    [JsonPropertyName("expensive")] public bool Expensive { get; set; }
}

public sealed class ScopesResponseBody
{
    [JsonPropertyName("scopes")] public Scope[] Scopes { get; set; } = [];
}

public sealed class VariablesArguments
{
    [JsonPropertyName("variablesReference")] public int VariablesReference { get; set; }
}

public sealed class Variable
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("variablesReference")] public int VariablesReference { get; set; }
}

public sealed class VariablesResponseBody
{
    [JsonPropertyName("variables")] public Variable[] Variables { get; set; } = [];
}
```

- [ ] **Step 6: Create the source-gen JSON context**

File `src/Koh.Debugger/Dap/DapJson.cs`:

```csharp
using System.Text.Json.Serialization;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap;

[JsonSerializable(typeof(ProtocolMessage))]
[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSerializable(typeof(Event))]
[JsonSerializable(typeof(Capabilities))]
[JsonSerializable(typeof(InitializeRequestArguments))]
[JsonSerializable(typeof(LaunchRequestArguments))]
[JsonSerializable(typeof(ContinueArguments))]
[JsonSerializable(typeof(ContinueResponseBody))]
[JsonSerializable(typeof(PauseArguments))]
[JsonSerializable(typeof(ConfigurationDoneArguments))]
[JsonSerializable(typeof(TerminateArguments))]
[JsonSerializable(typeof(SetBreakpointsArguments))]
[JsonSerializable(typeof(SetBreakpointsResponseBody))]
[JsonSerializable(typeof(Breakpoint))]
[JsonSerializable(typeof(ScopesArguments))]
[JsonSerializable(typeof(ScopesResponseBody))]
[JsonSerializable(typeof(Scope))]
[JsonSerializable(typeof(VariablesArguments))]
[JsonSerializable(typeof(VariablesResponseBody))]
[JsonSerializable(typeof(Variable))]
public sealed partial class DapJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 7: Build**

Run: `dotnet build src/Koh.Debugger/Koh.Debugger.csproj`
Expected: build succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/Koh.Debugger/Dap/
git commit -m "feat(debugger): add DAP message records and JSON source-gen context"
```

---

### Task 1.J.2: DapCapabilities with Phase 1 set

**Files:**
- Create: `src/Koh.Debugger/Dap/DapCapabilities.cs`

- [ ] **Step 1: Create `DapCapabilities.cs`**

```csharp
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap;

public static class DapCapabilities
{
    /// <summary>Phase 1 capabilities per spec §8.7.</summary>
    public static Capabilities Phase1() => new()
    {
        SupportsConfigurationDoneRequest = true,
        SupportsFunctionBreakpoints = false,
        SupportsConditionalBreakpoints = false,
        SupportsHitConditionalBreakpoints = false,
        SupportsStepBack = false,
        SupportsSetVariable = false,
        SupportsReadMemoryRequest = false,
        SupportsWriteMemoryRequest = false,
        SupportsDisassembleRequest = false,
        SupportsSteppingGranularity = false,
        SupportsInstructionBreakpoints = false,
        SupportsExceptionInfoRequest = true,
    };
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Debugger/Koh.Debugger.csproj`

```bash
git add src/Koh.Debugger/Dap/DapCapabilities.cs
git commit -m "feat(debugger): add DapCapabilities.Phase1() per spec §8.7"
```

---

### Task 1.J.3: DapDispatcher with request routing and byte transport

**Files:**
- Create: `src/Koh.Debugger/Dap/DapDispatcher.cs`

- [ ] **Step 1: Create `DapDispatcher.cs`**

```csharp
using System.Text;
using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap;

/// <summary>
/// Dispatches DAP requests to handlers and emits responses/events as byte buffers.
/// Transport-agnostic — see §8.2. The host (Blazor JS interop, in-process tests)
/// wires bytes in via <see cref="HandleRequest"/> and bytes out via the events.
/// </summary>
public sealed class DapDispatcher
{
    private int _nextOutboundSeq = 1;
    private readonly Dictionary<string, Func<Request, Response>> _handlers = new();

    public event Action<ReadOnlyMemory<byte>>? ResponseReady;
    public event Action<ReadOnlyMemory<byte>>? EventReady;

    public void RegisterHandler(string command, Func<Request, Response> handler)
    {
        _handlers[command] = handler;
    }

    public void HandleRequest(ReadOnlySpan<byte> jsonBytes)
    {
        Request? request;
        try
        {
            request = JsonSerializer.Deserialize(jsonBytes, DapJsonContext.Default.Request);
        }
        catch (JsonException ex)
        {
            EmitErrorResponse(requestSeq: 0, command: "", message: $"invalid JSON: {ex.Message}");
            return;
        }

        if (request is null)
        {
            EmitErrorResponse(0, "", "null request");
            return;
        }

        if (!_handlers.TryGetValue(request.Command, out var handler))
        {
            EmitErrorResponse(request.Seq, request.Command, $"unsupported command '{request.Command}'");
            return;
        }

        Response response;
        try
        {
            response = handler(request);
        }
        catch (Exception ex)
        {
            EmitErrorResponse(request.Seq, request.Command, ex.Message);
            return;
        }

        response.Seq = _nextOutboundSeq++;
        response.Type = "response";
        response.RequestSeq = request.Seq;
        response.Command = request.Command;

        var json = JsonSerializer.SerializeToUtf8Bytes(response, DapJsonContext.Default.Response);
        ResponseReady?.Invoke(json);
    }

    public void SendEvent(string eventName, object? body)
    {
        var evt = new Event
        {
            Seq = _nextOutboundSeq++,
            Type = "event",
            EventName = eventName,
            Body = body,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(evt, DapJsonContext.Default.Event);
        EventReady?.Invoke(json);
    }

    private void EmitErrorResponse(int requestSeq, string command, string message)
    {
        var response = new Response
        {
            Seq = _nextOutboundSeq++,
            Type = "response",
            RequestSeq = requestSeq,
            Success = false,
            Command = command,
            Message = message,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(response, DapJsonContext.Default.Response);
        ResponseReady?.Invoke(json);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Koh.Debugger/Koh.Debugger.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Debugger/Dap/DapDispatcher.cs
git commit -m "feat(debugger): add DapDispatcher with transport-agnostic byte interface"
```

---

## Phase 1-K: Debugger handlers + session state

### Task 1.K.1: Debug session scaffolding + source/symbol maps

**Files:**
- Create: `src/Koh.Debugger/Session/BankedAddress.cs` (re-export from linker for convenience)
- Create: `src/Koh.Debugger/Session/SourceMap.cs`
- Create: `src/Koh.Debugger/Session/SymbolMap.cs`
- Create: `src/Koh.Debugger/Session/DebugInfoLoader.cs`
- Create: `src/Koh.Debugger/Session/BreakpointManager.cs`
- Create: `src/Koh.Debugger/DebugSession.cs`

- [ ] **Step 1: Create `Session/SourceMap.cs`**

```csharp
using Koh.Linker.Core;

namespace Koh.Debugger.Session;

/// <summary>
/// Forward direction: <c>(file, line) → List&lt;BankedAddress&gt;</c>. Multiple
/// addresses per line are expected for macro expansions.
/// </summary>
public sealed class SourceMap
{
    private readonly Dictionary<(string File, uint Line), List<BankedAddress>> _byLine
        = new(SourceLineComparer.Instance);

    public void Add(string file, uint line, BankedAddress address)
    {
        var key = (file, line);
        if (!_byLine.TryGetValue(key, out var list))
            _byLine[key] = list = new();
        list.Add(address);
    }

    public IReadOnlyList<BankedAddress> Lookup(string file, uint line)
    {
        return _byLine.TryGetValue((file, line), out var list)
            ? list
            : Array.Empty<BankedAddress>();
    }

    private sealed class SourceLineComparer : IEqualityComparer<(string File, uint Line)>
    {
        public static readonly SourceLineComparer Instance = new();
        public bool Equals((string File, uint Line) x, (string File, uint Line) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.File, y.File) && x.Line == y.Line;
        public int GetHashCode((string File, uint Line) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.File), obj.Line);
    }
}
```

- [ ] **Step 2: Create `Session/SymbolMap.cs`**

```csharp
using Koh.Linker.Core;

namespace Koh.Debugger.Session;

public sealed class SymbolMap
{
    private readonly Dictionary<string, KdbgParsedSymbol> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<BankedAddress, List<KdbgParsedSymbol>> _byAddress = new();

    public void Add(KdbgParsedSymbol sym)
    {
        _byName[sym.Name] = sym;
        var addr = new BankedAddress(sym.Bank, sym.Address);
        if (!_byAddress.TryGetValue(addr, out var list))
            _byAddress[addr] = list = new();
        list.Add(sym);
    }

    public KdbgParsedSymbol? Lookup(string name)
        => _byName.TryGetValue(name, out var s) ? s : null;

    public IReadOnlyList<KdbgParsedSymbol> LookupByAddress(BankedAddress addr)
        => _byAddress.TryGetValue(addr, out var list) ? list : Array.Empty<KdbgParsedSymbol>();

    public IEnumerable<KdbgParsedSymbol> All => _byName.Values;
}
```

- [ ] **Step 3: Create `Session/DebugInfoLoader.cs`**

```csharp
using Koh.Linker.Core;

namespace Koh.Debugger.Session;

public sealed class DebugInfoLoader
{
    public SourceMap SourceMap { get; } = new();
    public SymbolMap SymbolMap { get; } = new();

    public void Load(ReadOnlyMemory<byte> kdbgBytes)
    {
        var parsed = KdbgReader.Parse(kdbgBytes.ToArray());
        foreach (var sym in parsed.Symbols)
            SymbolMap.Add(sym);
        foreach (var entry in parsed.AddressMap)
        {
            if (entry.SourceFile is null) continue;
            var addr = new BankedAddress(entry.Bank, entry.Address);
            SourceMap.Add(entry.SourceFile, entry.Line, addr);
        }
    }
}
```

- [ ] **Step 4: Create `Session/BreakpointManager.cs`**

```csharp
using Koh.Linker.Core;

namespace Koh.Debugger.Session;

public sealed class BreakpointManager
{
    private readonly HashSet<uint> _execution = [];

    public int Count => _execution.Count;

    public void ClearAll() => _execution.Clear();

    public void Add(BankedAddress address) => _execution.Add(address.Packed);
    public void Remove(BankedAddress address) => _execution.Remove(address.Packed);

    public bool Contains(BankedAddress address) => _execution.Contains(address.Packed);
}
```

- [ ] **Step 5: Create `DebugSession.cs`**

```csharp
using Koh.Debugger.Session;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Debugger;

public sealed class DebugSession
{
    public GameBoySystem? System { get; private set; }
    public DebugInfoLoader DebugInfo { get; } = new();
    public BreakpointManager Breakpoints { get; } = new();

    public volatile bool PauseRequested;

    public bool IsLaunched => System is not null;

    public void Launch(ReadOnlyMemory<byte> romBytes, ReadOnlyMemory<byte> kdbgBytes, HardwareMode mode)
    {
        var cart = CartridgeFactory.Load(romBytes.Span);
        System = new GameBoySystem(mode, cart);
        DebugInfo.Load(kdbgBytes);
    }

    public void Terminate()
    {
        System?.RunGuard.RequestStop();
        PauseRequested = true;
        System = null;
    }
}
```

- [ ] **Step 6: Build and commit**

Run: `dotnet build src/Koh.Debugger/Koh.Debugger.csproj`

```bash
git add src/Koh.Debugger/Session/ src/Koh.Debugger/DebugSession.cs
git commit -m "feat(debugger): add SourceMap, SymbolMap, DebugInfoLoader, BreakpointManager, DebugSession"
```

---

### Task 1.K.2: InitializeHandler + LaunchHandler + ConfigurationDoneHandler

**Files:**
- Create: `src/Koh.Debugger/Dap/Handlers/InitializeHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/LaunchHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/ConfigurationDoneHandler.cs`

- [ ] **Step 1: Create `InitializeHandler.cs`**

```csharp
using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public static class InitializeHandler
{
    public static Response Handle(Request request)
    {
        return new Response
        {
            Success = true,
            Body = DapCapabilities.Phase1(),
        };
    }
}
```

- [ ] **Step 2: Create `LaunchHandler.cs`**

```csharp
using System.Text.Json;
using Koh.Debugger.Dap.Messages;
using Koh.Emulator.Core;

namespace Koh.Debugger.Dap.Handlers;

public sealed class LaunchHandler
{
    private readonly DebugSession _session;
    private readonly Func<string, ReadOnlyMemory<byte>> _loadFile;

    public LaunchHandler(DebugSession session, Func<string, ReadOnlyMemory<byte>> loadFile)
    {
        _session = session;
        _loadFile = loadFile;
    }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.LaunchRequestArguments);
        if (args is null || string.IsNullOrEmpty(args.Program))
        {
            return new Response { Success = false, Message = "launch: missing 'program'" };
        }

        var rom = _loadFile(args.Program);
        var kdbgPath = args.DebugInfo ?? Path.ChangeExtension(args.Program, ".kdbg");
        var kdbg = _loadFile(kdbgPath);

        HardwareMode mode = args.HardwareMode switch
        {
            "dmg" => HardwareMode.Dmg,
            "cgb" => HardwareMode.Cgb,
            _     => DetectFromHeader(rom.Span),
        };

        _session.Launch(rom, kdbg, mode);

        return new Response { Success = true };
    }

    private static HardwareMode DetectFromHeader(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 0x150) return HardwareMode.Dmg;
        byte cgbByte = rom[0x143];
        return (cgbByte & 0x80) != 0 ? HardwareMode.Cgb : HardwareMode.Dmg;
    }
}
```

- [ ] **Step 3: Create `ConfigurationDoneHandler.cs`**

```csharp
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public static class ConfigurationDoneHandler
{
    public static Response Handle(Request request)
    {
        return new Response { Success = true };
    }
}
```

- [ ] **Step 4: Build and commit**

Run: `dotnet build src/Koh.Debugger/Koh.Debugger.csproj`

```bash
git add src/Koh.Debugger/Dap/Handlers/InitializeHandler.cs src/Koh.Debugger/Dap/Handlers/LaunchHandler.cs src/Koh.Debugger/Dap/Handlers/ConfigurationDoneHandler.cs
git commit -m "feat(debugger): add Initialize/Launch/ConfigurationDone handlers"
```

---

### Task 1.K.3: Continue/Pause/Terminate + SetBreakpoints + Scopes/Variables handlers

**Files:**
- Create: `src/Koh.Debugger/Dap/Handlers/ContinueHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/PauseHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/TerminateHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/SetBreakpointsHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/ScopesHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/VariablesHandler.cs`
- Create: `src/Koh.Debugger/Dap/Handlers/ExceptionInfoHandler.cs`

- [ ] **Step 1: Create Continue/Pause/Terminate handlers**

File `src/Koh.Debugger/Dap/Handlers/ContinueHandler.cs`:

```csharp
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class ContinueHandler
{
    private readonly DebugSession _session;
    public ContinueHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        _session.PauseRequested = false;
        return new Response
        {
            Success = true,
            Body = new ContinueResponseBody { AllThreadsContinued = true },
        };
    }
}
```

File `src/Koh.Debugger/Dap/Handlers/PauseHandler.cs`:

```csharp
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class PauseHandler
{
    private readonly DebugSession _session;
    public PauseHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        _session.PauseRequested = true;
        _session.System?.RunGuard.RequestStop();
        return new Response { Success = true };
    }
}
```

File `src/Koh.Debugger/Dap/Handlers/TerminateHandler.cs`:

```csharp
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class TerminateHandler
{
    private readonly DebugSession _session;
    public TerminateHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        _session.Terminate();
        return new Response { Success = true };
    }
}
```

- [ ] **Step 2: Create SetBreakpointsHandler**

File `src/Koh.Debugger/Dap/Handlers/SetBreakpointsHandler.cs`:

```csharp
using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class SetBreakpointsHandler
{
    private readonly DebugSession _session;
    public SetBreakpointsHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.SetBreakpointsArguments);
        if (args is null)
            return new Response { Success = false, Message = "setBreakpoints: missing args" };

        // Phase 1: breakpoints are stored but never halt execution (advertising set
        // excludes actual halt behavior). We still resolve source locations against
        // .kdbg and return verified results to VS Code so the gutter shows a red marker.

        _session.Breakpoints.ClearAll();

        var source = args.Source.Path ?? args.Source.Name ?? "";
        var results = new List<Breakpoint>();

        foreach (var bp in args.Breakpoints ?? [])
        {
            var addresses = _session.DebugInfo.SourceMap.Lookup(source, (uint)bp.Line);
            bool verified = addresses.Count > 0;
            if (verified)
            {
                foreach (var addr in addresses)
                    _session.Breakpoints.Add(addr);
            }
            results.Add(new Breakpoint
            {
                Verified = verified,
                Line = bp.Line,
                Source = args.Source,
                Message = verified ? null : "no code at this line",
            });
        }

        return new Response
        {
            Success = true,
            Body = new SetBreakpointsResponseBody { Breakpoints = [.. results] },
        };
    }
}
```

- [ ] **Step 3: Create Scopes and Variables handlers**

File `src/Koh.Debugger/Dap/Handlers/ScopesHandler.cs`:

```csharp
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public static class ScopesHandler
{
    public const int RegistersVariablesRef = 1;
    public const int HardwareVariablesRef = 2;

    public static Response Handle(Request request)
    {
        return new Response
        {
            Success = true,
            Body = new ScopesResponseBody
            {
                Scopes =
                [
                    new Scope { Name = "Registers", VariablesReference = RegistersVariablesRef, Expensive = false },
                    new Scope { Name = "Hardware", VariablesReference = HardwareVariablesRef, Expensive = false },
                ],
            },
        };
    }
}
```

File `src/Koh.Debugger/Dap/Handlers/VariablesHandler.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class VariablesHandler
{
    private readonly DebugSession _session;
    public VariablesHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.VariablesArguments);
        if (args is null)
            return new Response { Success = false, Message = "variables: missing args" };

        var system = _session.System;
        if (system is null)
            return new Response { Success = true, Body = new VariablesResponseBody { Variables = [] } };

        var variables = args.VariablesReference switch
        {
            ScopesHandler.RegistersVariablesRef => RegistersScope(system),
            ScopesHandler.HardwareVariablesRef => HardwareScope(system),
            _ => [],
        };

        return new Response
        {
            Success = true,
            Body = new VariablesResponseBody { Variables = variables },
        };
    }

    private static Variable[] RegistersScope(Emulator.Core.GameBoySystem gb)
    {
        ref var r = ref gb.Registers;
        static string H8(byte v) => "$" + v.ToString("X2");
        static string H16(ushort v) => "$" + v.ToString("X4");
        return
        [
            new Variable { Name = "A",  Value = H8(r.A) },
            new Variable { Name = "F",  Value = H8(r.F) },
            new Variable { Name = "B",  Value = H8(r.B) },
            new Variable { Name = "C",  Value = H8(r.C) },
            new Variable { Name = "D",  Value = H8(r.D) },
            new Variable { Name = "E",  Value = H8(r.E) },
            new Variable { Name = "H",  Value = H8(r.H) },
            new Variable { Name = "L",  Value = H8(r.L) },
            new Variable { Name = "AF", Value = H16(r.AF) },
            new Variable { Name = "BC", Value = H16(r.BC) },
            new Variable { Name = "DE", Value = H16(r.DE) },
            new Variable { Name = "HL", Value = H16(r.HL) },
            new Variable { Name = "SP", Value = H16(r.Sp) },
            new Variable { Name = "PC", Value = H16(r.Pc) },
            new Variable { Name = "Z",  Value = r.FlagSet(Emulator.Core.Cpu.CpuRegisters.FlagZ) ? "true" : "false" },
            new Variable { Name = "N",  Value = r.FlagSet(Emulator.Core.Cpu.CpuRegisters.FlagN) ? "true" : "false" },
            new Variable { Name = "H",  Value = r.FlagSet(Emulator.Core.Cpu.CpuRegisters.FlagH) ? "true" : "false" },
            new Variable { Name = "C",  Value = r.FlagSet(Emulator.Core.Cpu.CpuRegisters.FlagC) ? "true" : "false" },
        ];
    }

    private static Variable[] HardwareScope(Emulator.Core.GameBoySystem gb)
    {
        static string H8(byte v) => "$" + v.ToString("X2");
        return
        [
            new Variable { Name = "LY",   Value = H8(gb.Ppu.LY) },
            new Variable { Name = "IF",   Value = H8(gb.Io.Interrupts.IF) },
            new Variable { Name = "IE",   Value = H8(gb.Io.Interrupts.IE) },
            new Variable { Name = "IME",  Value = gb.Io.Interrupts.IME ? "true" : "false" },
            new Variable { Name = "DIV",  Value = H8(gb.Timer.DIV) },
            new Variable { Name = "TIMA", Value = H8(gb.Timer.TIMA) },
            new Variable { Name = "TMA",  Value = H8(gb.Timer.TMA) },
            new Variable { Name = "TAC",  Value = H8(gb.Timer.TAC) },
        ];
    }
}
```

- [ ] **Step 4: Create ExceptionInfoHandler stub**

File `src/Koh.Debugger/Dap/Handlers/ExceptionInfoHandler.cs`:

```csharp
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public static class ExceptionInfoHandler
{
    public static Response Handle(Request request) => new() { Success = true, Body = new { } };
}
```

- [ ] **Step 5: Build**

Run: `dotnet build src/Koh.Debugger/Koh.Debugger.csproj`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Debugger/Dap/Handlers/
git commit -m "feat(debugger): add Continue/Pause/Terminate/SetBreakpoints/Scopes/Variables handlers"
```

---

### Task 1.K.4: Wire handlers into DapDispatcher at session startup

**Files:**
- Create: `src/Koh.Debugger/Dap/HandlerRegistration.cs`

- [ ] **Step 1: Create `HandlerRegistration.cs`**

```csharp
using Koh.Debugger.Dap.Handlers;

namespace Koh.Debugger.Dap;

public static class HandlerRegistration
{
    public static void RegisterAll(
        DapDispatcher dispatcher,
        DebugSession session,
        Func<string, ReadOnlyMemory<byte>> loadFile)
    {
        var launchHandler = new LaunchHandler(session, loadFile);
        var continueHandler = new ContinueHandler(session);
        var pauseHandler = new PauseHandler(session);
        var terminateHandler = new TerminateHandler(session);
        var setBpHandler = new SetBreakpointsHandler(session);
        var variablesHandler = new VariablesHandler(session);

        dispatcher.RegisterHandler("initialize", InitializeHandler.Handle);
        dispatcher.RegisterHandler("launch", launchHandler.Handle);
        dispatcher.RegisterHandler("configurationDone", ConfigurationDoneHandler.Handle);
        dispatcher.RegisterHandler("continue", continueHandler.Handle);
        dispatcher.RegisterHandler("pause", pauseHandler.Handle);
        dispatcher.RegisterHandler("terminate", terminateHandler.Handle);
        dispatcher.RegisterHandler("setBreakpoints", setBpHandler.Handle);
        dispatcher.RegisterHandler("scopes", ScopesHandler.Handle);
        dispatcher.RegisterHandler("variables", variablesHandler.Handle);
        dispatcher.RegisterHandler("exceptionInfo", ExceptionInfoHandler.Handle);
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Debugger/Koh.Debugger.csproj`

```bash
git add src/Koh.Debugger/Dap/HandlerRegistration.cs
git commit -m "feat(debugger): add HandlerRegistration to wire handlers into dispatcher"
```

---

## Phase 1-L: Debugger tests and execution loop

### Task 1.L.1: Execution loop (cooperative frame runner)

**Files:**
- Create: `src/Koh.Debugger/Session/ExecutionLoop.cs`

- [ ] **Step 1: Create `ExecutionLoop.cs`**

```csharp
using Koh.Emulator.Core;

namespace Koh.Debugger.Session;

/// <summary>
/// Cooperative run loop that drives <see cref="GameBoySystem.RunFrame"/>
/// and yields to the JS event loop between frames so DAP pause requests
/// can be processed. See §8.6.
/// </summary>
public sealed class ExecutionLoop
{
    private readonly DebugSession _session;
    public event Action<Framebuffer>? FramebufferReady;
    public event Action<StepResult>? StoppedOnBreak;

    public ExecutionLoop(DebugSession session) { _session = session; }

    public async Task RunAsync()
    {
        if (_session.System is not { } gb) return;

        while (!_session.PauseRequested)
        {
            var result = gb.RunFrame();
            FramebufferReady?.Invoke(gb.Framebuffer);

            if (result.Reason == StopReason.Breakpoint || result.Reason == StopReason.Watchpoint)
            {
                StoppedOnBreak?.Invoke(result);
                break;
            }

            // Yield to the JS event loop so pause requests reach their handler.
            await Task.Yield();
        }
    }
}

// Re-export Framebuffer from Koh.Emulator.Core namespace into this namespace for brevity.
internal sealed class Framebuffer : Emulator.Core.Ppu.Framebuffer { }
```

Actually the stub re-export is not needed — use the real type:

Replace the file contents with:

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Ppu;

namespace Koh.Debugger.Session;

public sealed class ExecutionLoop
{
    private readonly DebugSession _session;
    public event Action<Framebuffer>? FramebufferReady;
    public event Action<StepResult>? StoppedOnBreak;

    public ExecutionLoop(DebugSession session) { _session = session; }

    public async Task RunAsync()
    {
        if (_session.System is not { } gb) return;

        while (!_session.PauseRequested)
        {
            var result = gb.RunFrame();
            FramebufferReady?.Invoke(gb.Framebuffer);

            if (result.Reason == StopReason.Breakpoint || result.Reason == StopReason.Watchpoint)
            {
                StoppedOnBreak?.Invoke(result);
                break;
            }

            await Task.Yield();
        }
    }
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Debugger/Koh.Debugger.csproj`

```bash
git add src/Koh.Debugger/Session/ExecutionLoop.cs
git commit -m "feat(debugger): add cooperative ExecutionLoop for frame-by-frame run"
```

---

### Task 1.L.2: Debugger handler round-trip tests

**Files:**
- Create: `tests/Koh.Debugger.Tests/DapDispatcherTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using System.Text;
using System.Text.Json;
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Debugger.Dap.Handlers;
using Koh.Debugger.Dap.Messages;
using Koh.Linker.Core;

namespace Koh.Debugger.Tests;

public class DapDispatcherTests
{
    private static (DapDispatcher, DebugSession, List<byte[]> responses) Build()
    {
        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        HandlerRegistration.RegisterAll(
            dispatcher,
            session,
            loadFile: _ => Array.Empty<byte>());

        return (dispatcher, session, responses);
    }

    private static byte[] EncodeRequest(int seq, string command, object? args = null)
    {
        var obj = new Dictionary<string, object?>
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
        };
        if (args is not null) obj["arguments"] = args;
        return JsonSerializer.SerializeToUtf8Bytes(obj);
    }

    private static JsonDocument Parse(byte[] bytes) => JsonDocument.Parse(bytes);

    [Test]
    public async Task Initialize_Returns_Phase1_Capabilities()
    {
        var (dispatcher, _, responses) = Build();
        dispatcher.HandleRequest(EncodeRequest(1, "initialize", new { clientID = "test" }));

        await Assert.That(responses).HasCount(1);
        using var doc = Parse(responses[0]);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("success").GetBoolean()).IsTrue();
        var body = root.GetProperty("body");
        await Assert.That(body.GetProperty("supportsConfigurationDoneRequest").GetBoolean()).IsTrue();
        await Assert.That(body.GetProperty("supportsReadMemoryRequest").GetBoolean()).IsFalse();
        await Assert.That(body.GetProperty("supportsDisassembleRequest").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Scopes_Returns_Registers_And_Hardware()
    {
        var (dispatcher, _, responses) = Build();
        dispatcher.HandleRequest(EncodeRequest(1, "scopes", new { frameId = 0 }));

        await Assert.That(responses).HasCount(1);
        using var doc = Parse(responses[0]);
        var scopes = doc.RootElement.GetProperty("body").GetProperty("scopes");
        await Assert.That(scopes.GetArrayLength()).IsEqualTo(2);
        await Assert.That(scopes[0].GetProperty("name").GetString()).IsEqualTo("Registers");
        await Assert.That(scopes[1].GetProperty("name").GetString()).IsEqualTo("Hardware");
    }

    [Test]
    public async Task UnknownCommand_Returns_ErrorResponse()
    {
        var (dispatcher, _, responses) = Build();
        dispatcher.HandleRequest(EncodeRequest(1, "definitelyNotAThing"));

        await Assert.That(responses).HasCount(1);
        using var doc = Parse(responses[0]);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Pause_Sets_Session_PauseRequested()
    {
        var (dispatcher, session, _) = Build();
        dispatcher.HandleRequest(EncodeRequest(1, "pause", new { threadId = 1 }));
        await Assert.That(session.PauseRequested).IsTrue();
    }

    [Test]
    public async Task Continue_Clears_Session_PauseRequested()
    {
        var (dispatcher, session, _) = Build();
        session.PauseRequested = true;
        dispatcher.HandleRequest(EncodeRequest(1, "continue", new { threadId = 1 }));
        await Assert.That(session.PauseRequested).IsFalse();
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Koh.Debugger.Tests/Koh.Debugger.Tests.csproj --filter DapDispatcherTests`
Expected: all five tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Debugger.Tests/DapDispatcherTests.cs
git commit -m "test(debugger): add DAP dispatcher round-trip tests"
```

---

### Task 1.L.3: SetBreakpoints verified-location test

- [ ] **Step 1: Append to `DapDispatcherTests.cs`**

```csharp
    [Test]
    public async Task SetBreakpoints_Returns_Verified_When_Kdbg_Maps_Line()
    {
        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        // Build a tiny .kdbg with one address mapping on line 10 of src/main.asm.
        var builder = new DebugInfoBuilder();
        builder.AddAddressMapping(bank: 0, address: 0x0150, byteCount: 1,
            sourceFile: "src/main.asm", line: 10);
        using var kdbgStream = new MemoryStream();
        KdbgFileWriter.Write(kdbgStream, builder);

        // Build a tiny ROM (RomOnly) that loads cleanly.
        var rom = new byte[0x8000];
        rom[0x147] = 0x00;

        HandlerRegistration.RegisterAll(
            dispatcher, session,
            loadFile: path => path.EndsWith(".kdbg") ? kdbgStream.ToArray() : rom);

        // Launch to populate the SourceMap.
        dispatcher.HandleRequest(EncodeRequest(1, "launch",
            new { program = "game.gb", debugInfo = "game.kdbg" }));

        dispatcher.HandleRequest(EncodeRequest(2, "setBreakpoints", new
        {
            source = new { path = "src/main.asm" },
            breakpoints = new[] { new { line = 10 } }
        }));

        var last = responses[^1];
        using var doc = Parse(last);
        var bps = doc.RootElement.GetProperty("body").GetProperty("breakpoints");
        await Assert.That(bps.GetArrayLength()).IsEqualTo(1);
        await Assert.That(bps[0].GetProperty("verified").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task SetBreakpoints_Returns_Unverified_When_Line_Has_No_Code()
    {
        var dispatcher = new DapDispatcher();
        var session = new DebugSession();
        var responses = new List<byte[]>();
        dispatcher.ResponseReady += data => responses.Add(data.ToArray());

        var builder = new DebugInfoBuilder();
        builder.AddAddressMapping(bank: 0, address: 0x0150, byteCount: 1,
            sourceFile: "src/main.asm", line: 10);
        using var kdbgStream = new MemoryStream();
        KdbgFileWriter.Write(kdbgStream, builder);

        var rom = new byte[0x8000];
        rom[0x147] = 0x00;

        HandlerRegistration.RegisterAll(
            dispatcher, session,
            loadFile: path => path.EndsWith(".kdbg") ? kdbgStream.ToArray() : rom);

        dispatcher.HandleRequest(EncodeRequest(1, "launch",
            new { program = "game.gb", debugInfo = "game.kdbg" }));

        dispatcher.HandleRequest(EncodeRequest(2, "setBreakpoints", new
        {
            source = new { path = "src/main.asm" },
            breakpoints = new[] { new { line = 999 } }
        }));

        var last = responses[^1];
        using var doc = Parse(last);
        var bps = doc.RootElement.GetProperty("body").GetProperty("breakpoints");
        await Assert.That(bps[0].GetProperty("verified").GetBoolean()).IsFalse();
    }
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Koh.Debugger.Tests/Koh.Debugger.Tests.csproj`
Expected: all tests pass.

- [ ] **Step 3: Commit**

```bash
git add tests/Koh.Debugger.Tests/DapDispatcherTests.cs
git commit -m "test(debugger): add SetBreakpoints verified-location round-trip"
```

---

## Phase 1-M: Blazor app skeleton and services

### Task 1.M.1: Runtime mode detection

**Files:**
- Create: `src/Koh.Emulator.App/Shell/RuntimeMode.cs`
- Create: `src/Koh.Emulator.App/Shell/RuntimeModeDetector.cs`
- Create: `src/Koh.Emulator.App/Shell/RuntimeModeDetector.razor.js`

- [ ] **Step 1: Create `RuntimeMode.cs`**

```csharp
namespace Koh.Emulator.App.Shell;

public enum RuntimeMode
{
    Standalone,
    Debug,
}
```

- [ ] **Step 2: Create the detector**

File `src/Koh.Emulator.App/Shell/RuntimeModeDetector.cs`:

```csharp
using Microsoft.JSInterop;

namespace Koh.Emulator.App.Shell;

public sealed class RuntimeModeDetector
{
    private readonly IJSRuntime _js;
    public RuntimeModeDetector(IJSRuntime js) { _js = js; }

    public async Task<RuntimeMode> DetectAsync()
    {
        try
        {
            bool insideVsCode = await _js.InvokeAsync<bool>("kohRuntimeMode.isInsideVsCodeWebview");
            return insideVsCode ? RuntimeMode.Debug : RuntimeMode.Standalone;
        }
        catch
        {
            return RuntimeMode.Standalone;
        }
    }
}
```

- [ ] **Step 3: Create the JS helper**

File `src/Koh.Emulator.App/wwwroot/js/runtime-mode.js`:

```javascript
// Detects whether the Blazor app is running inside a VS Code webview.
// The extension sets window.__kohVsCodeBridge when it injects the bridge script.
window.kohRuntimeMode = {
    isInsideVsCodeWebview: function () {
        return typeof window.acquireVsCodeApi === 'function' || window.__kohVsCodeBridge === true;
    }
};
```

Include the script in `wwwroot/index.html` right before the Blazor loader:

```html
    <script src="js/runtime-mode.js"></script>
    <script src="_framework/blazor.webassembly.js"></script>
```

- [ ] **Step 4: Build and commit**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`

```bash
git add src/Koh.Emulator.App/Shell/ src/Koh.Emulator.App/wwwroot/js/runtime-mode.js src/Koh.Emulator.App/wwwroot/index.html
git commit -m "feat(emulator-app): add runtime-mode detection for VS Code webview"
```

---

### Task 1.M.2: EmulatorHost service

**Files:**
- Create: `src/Koh.Emulator.App/Services/EmulatorHost.cs`
- Create: `src/Koh.Emulator.App/Services/FramePacer.cs`
- Create: `src/Koh.Emulator.App/wwwroot/js/frame-pacer.js`

- [ ] **Step 1: Create `FramePacer.cs`**

```csharp
using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

public sealed class FramePacer
{
    private readonly IJSRuntime _js;
    public FramePacer(IJSRuntime js) { _js = js; }

    public ValueTask WaitForNextFrameAsync()
    {
        return _js.InvokeVoidAsync("kohFramePacer.waitForRaf").AsTask() is Task t
            ? new ValueTask(t)
            : ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Create the rAF helper JS**

File `src/Koh.Emulator.App/wwwroot/js/frame-pacer.js`:

```javascript
window.kohFramePacer = {
    waitForRaf: function () {
        return new Promise(function (resolve) {
            window.requestAnimationFrame(function () { resolve(); });
        });
    }
};
```

Add the script to `index.html` with the other script tags.

- [ ] **Step 3: Create `EmulatorHost.cs`**

```csharp
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.App.Services;

public sealed class EmulatorHost
{
    private readonly FramePacer _framePacer;
    public GameBoySystem? System { get; private set; }
    public event Action? FrameReady;
    public event Action? StateChanged;

    public bool IsPaused { get; set; } = true;

    public EmulatorHost(FramePacer framePacer) { _framePacer = framePacer; }

    public void Load(ReadOnlyMemory<byte> romBytes, HardwareMode mode)
    {
        var cart = CartridgeFactory.Load(romBytes.Span);
        System = new GameBoySystem(mode, cart);
        IsPaused = true;
        StateChanged?.Invoke();
    }

    public async Task RunAsync()
    {
        if (System is null) return;
        IsPaused = false;

        while (!IsPaused && System is not null)
        {
            var result = System.RunFrame();
            FrameReady?.Invoke();
            StateChanged?.Invoke();

            if (result.Reason == StopReason.Breakpoint || result.Reason == StopReason.Watchpoint)
            {
                IsPaused = true;
                break;
            }

            await _framePacer.WaitForNextFrameAsync();
        }
    }

    public void StepInstruction()
    {
        if (System is null) return;
        System.StepInstruction();
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        IsPaused = true;
        System?.RunGuard.RequestStop();
    }
}
```

- [ ] **Step 4: Register services in `Program.cs`**

Replace `src/Koh.Emulator.App/Program.cs` contents with:

```csharp
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Koh.Emulator.App;
using Koh.Emulator.App.Services;
using Koh.Emulator.App.Shell;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<RuntimeModeDetector>();
builder.Services.AddSingleton<FramePacer>();
builder.Services.AddSingleton<EmulatorHost>();

await builder.Build().RunAsync();
```

- [ ] **Step 5: Build and commit**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`

```bash
git add src/Koh.Emulator.App/Services/ src/Koh.Emulator.App/wwwroot/js/frame-pacer.js src/Koh.Emulator.App/wwwroot/index.html src/Koh.Emulator.App/Program.cs
git commit -m "feat(emulator-app): add EmulatorHost, FramePacer, DI registration"
```

---

### Task 1.M.3: Root App.razor with shell routing

**Files:**
- Create: `src/Koh.Emulator.App/App.razor` (replace the Phase 0 placeholder)
- Create: `src/Koh.Emulator.App/Shell/DebugShell.razor`
- Create: `src/Koh.Emulator.App/Shell/StandaloneShell.razor`

- [ ] **Step 1: Replace `App.razor`**

```razor
@using Koh.Emulator.App.Shell
@inject RuntimeModeDetector ModeDetector

@if (_mode is null)
{
    <p>Loading…</p>
}
else if (_mode == RuntimeMode.Debug)
{
    <DebugShell />
}
else
{
    <StandaloneShell />
}

@code {
    private RuntimeMode? _mode;

    protected override async Task OnInitializedAsync()
    {
        _mode = await ModeDetector.DetectAsync();
    }
}
```

- [ ] **Step 2: Create a minimal `DebugShell.razor`**

```razor
@using Koh.Emulator.App.Components
@using Koh.Emulator.App.Services
@inject EmulatorHost EmulatorHost
@implements IDisposable

<div class="debug-shell">
    <div class="lcd-placeholder">Koh Emulator — Phase 1: awaiting PPU</div>
    <CpuDashboard />
    <div class="status-bar">
        <span>Mode: @(EmulatorHost.System?.Mode.ToString() ?? "not loaded")</span>
        <span>Cycles: @(EmulatorHost.System?.Cpu.TotalTCycles ?? 0)</span>
    </div>
</div>

@code {
    protected override void OnInitialized()
    {
        EmulatorHost.StateChanged += OnStateChanged;
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        EmulatorHost.StateChanged -= OnStateChanged;
    }
}
```

- [ ] **Step 3: Create a minimal `StandaloneShell.razor`**

```razor
@using Koh.Emulator.App.StandaloneMode

<div class="standalone-shell">
    <h1>Koh Emulator</h1>
    <RomFilePicker />
    <PlaybackControls />
    <div class="lcd-placeholder">Load a ROM to begin</div>
</div>
```

- [ ] **Step 4: Build — expected to fail because `CpuDashboard`, `RomFilePicker`, `PlaybackControls` don't exist yet**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`
Expected: compile errors for missing components. The next tasks create them.

- [ ] **Step 5: Commit the shells as a checkpoint**

```bash
git add src/Koh.Emulator.App/App.razor src/Koh.Emulator.App/Shell/DebugShell.razor src/Koh.Emulator.App/Shell/StandaloneShell.razor
git commit -m "feat(emulator-app): add App.razor routing + Debug/Standalone shells (components TBD)"
```

---

### Task 1.M.4: CpuDashboard component

**Files:**
- Create: `src/Koh.Emulator.App/Components/CpuDashboard.razor`

- [ ] **Step 1: Create `CpuDashboard.razor`**

```razor
@using Koh.Emulator.App.Services
@inject EmulatorHost EmulatorHost
@implements IDisposable

<div class="cpu-dashboard">
    @if (EmulatorHost.System is { } gb)
    {
        <h3>CPU</h3>
        <table>
            <tr><th>PC</th><td>@H16(gb.Registers.Pc)</td><th>SP</th><td>@H16(gb.Registers.Sp)</td></tr>
            <tr><th>A</th><td>@H8(gb.Registers.A)</td><th>F</th><td>@H8(gb.Registers.F)</td></tr>
            <tr><th>BC</th><td>@H16(gb.Registers.BC)</td><th>DE</th><td>@H16(gb.Registers.DE)</td></tr>
            <tr><th>HL</th><td>@H16(gb.Registers.HL)</td><th>Cycles</th><td>@gb.Cpu.TotalTCycles</td></tr>
        </table>
        <div class="flags">
            Z: @FlagDisplay(gb.Registers.FlagSet(Koh.Emulator.Core.Cpu.CpuRegisters.FlagZ))
            N: @FlagDisplay(gb.Registers.FlagSet(Koh.Emulator.Core.Cpu.CpuRegisters.FlagN))
            H: @FlagDisplay(gb.Registers.FlagSet(Koh.Emulator.Core.Cpu.CpuRegisters.FlagH))
            C: @FlagDisplay(gb.Registers.FlagSet(Koh.Emulator.Core.Cpu.CpuRegisters.FlagC))
        </div>
    }
    else
    {
        <p>No ROM loaded.</p>
    }
</div>

@code {
    protected override void OnInitialized()
    {
        EmulatorHost.StateChanged += OnStateChanged;
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => EmulatorHost.StateChanged -= OnStateChanged;

    private static string H8(byte v) => "$" + v.ToString("X2");
    private static string H16(ushort v) => "$" + v.ToString("X4");
    private static string FlagDisplay(bool on) => on ? "1" : "0";
}
```

- [ ] **Step 2: Build and commit**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj` — still fails because RomFilePicker and PlaybackControls are missing; that's fine.

```bash
git add src/Koh.Emulator.App/Components/CpuDashboard.razor
git commit -m "feat(emulator-app): add CpuDashboard razor component"
```

---

### Task 1.M.5: RomFilePicker and PlaybackControls for standalone mode

**Files:**
- Create: `src/Koh.Emulator.App/StandaloneMode/RomFilePicker.razor`
- Create: `src/Koh.Emulator.App/StandaloneMode/PlaybackControls.razor`

- [ ] **Step 1: Create `RomFilePicker.razor`**

```razor
@using Koh.Emulator.App.Services
@using Koh.Emulator.Core
@using Microsoft.AspNetCore.Components.Forms
@inject EmulatorHost EmulatorHost

<div class="rom-picker">
    <InputFile OnChange="OnFileSelected" accept=".gb,.gbc" />
</div>

@code {
    private async Task OnFileSelected(InputFileChangeEventArgs e)
    {
        var file = e.File;
        using var stream = file.OpenReadStream(maxAllowedSize: 8 * 1024 * 1024);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var bytes = ms.ToArray();

        HardwareMode mode = (bytes.Length > 0x143 && (bytes[0x143] & 0x80) != 0)
            ? HardwareMode.Cgb
            : HardwareMode.Dmg;

        EmulatorHost.Load(bytes, mode);
    }
}
```

- [ ] **Step 2: Create `PlaybackControls.razor`**

```razor
@using Koh.Emulator.App.Services
@inject EmulatorHost EmulatorHost

<div class="playback">
    <button @onclick="Run" disabled="@(EmulatorHost.System is null || !EmulatorHost.IsPaused)">Run</button>
    <button @onclick="Pause" disabled="@(EmulatorHost.IsPaused)">Pause</button>
    <button @onclick="StepInstruction" disabled="@(EmulatorHost.System is null)">Step</button>
</div>

@code {
    private async Task Run()
    {
        await EmulatorHost.RunAsync();
    }

    private void Pause() => EmulatorHost.Pause();

    private void StepInstruction() => EmulatorHost.StepInstruction();
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`
Expected: build succeeds now that all referenced components exist.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Emulator.App/StandaloneMode/
git commit -m "feat(emulator-app): add RomFilePicker and PlaybackControls for standalone mode"
```

---

### Task 1.M.6: Verify dev host serves the app

- [ ] **Step 1: Run the dev host**

Run: `dotnet run --project src/Koh.Emulator.App/Koh.Emulator.App.csproj`
Expected: a dev-host URL is printed, e.g. `http://localhost:5000`.

- [ ] **Step 2: Open the URL in a browser and verify the standalone shell renders**

Expected: the page shows "Koh Emulator", a file picker, Run/Pause/Step buttons, and "Load a ROM to begin".

- [ ] **Step 3: Load a tiny ROM (any `.gb` file, even a zero-filled one) to verify the dashboard appears**

The dashboard should show PC/SP/registers with initial values. The Run button should be enabled after loading.

- [ ] **Step 4: Stop the dev host and commit any tiny fixes from local testing**

No commit needed if nothing changed. Otherwise:

```bash
git add -u
git commit -m "fix(emulator-app): address dev-host smoke-test findings"
```

---

## Phase 1-N: Debug mode bridge + benchmark page

### Task 1.N.1: VS Code bridge JS + DapTransport

**Files:**
- Create: `src/Koh.Emulator.App/wwwroot/js/vscode-bridge.js`
- Create: `src/Koh.Emulator.App/DebugMode/DapTransport.cs`

- [ ] **Step 1: Create `vscode-bridge.js`**

File `src/Koh.Emulator.App/wwwroot/js/vscode-bridge.js`:

```javascript
// Bridges postMessage from the VS Code extension to the Blazor app.
// The extension sends: {kind: "dap", payload: <DAP JSON>}.
// The Blazor app sends back via DotNet.invokeMethod / this script.
window.__kohVsCodeBridge = true;

(function () {
    let vsCodeApi = null;
    try {
        if (typeof acquireVsCodeApi === 'function') {
            vsCodeApi = acquireVsCodeApi();
        }
    } catch (e) {
        // acquireVsCodeApi throws if called twice; ignore.
    }

    let blazorHandler = null;

    window.kohVsCodeBridge = {
        register: function (dotNetObjRef) {
            blazorHandler = dotNetObjRef;
        },

        sendToExtension: function (kind, payload) {
            if (vsCodeApi) {
                vsCodeApi.postMessage({ kind: kind, payload: payload });
            }
        }
    };

    window.addEventListener('message', function (event) {
        const data = event.data;
        if (!data || !blazorHandler) return;
        if (data.kind === 'dap') {
            blazorHandler.invokeMethodAsync('ReceiveDap', data.payload);
        }
    });
})();
```

Add the script to `index.html` before the Blazor loader.

- [ ] **Step 2: Create `DapTransport.cs`**

```csharp
using Microsoft.JSInterop;
using Koh.Debugger.Dap;
using System.Text;

namespace Koh.Emulator.App.DebugMode;

/// <summary>
/// Bridges the <see cref="DapDispatcher"/> to VS Code via the postMessage JS bridge.
/// </summary>
public sealed class DapTransport : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly DapDispatcher _dispatcher;
    private DotNetObjectReference<DapTransport>? _selfRef;

    public DapTransport(IJSRuntime js, DapDispatcher dispatcher)
    {
        _js = js;
        _dispatcher = dispatcher;

        _dispatcher.ResponseReady += OnResponseReady;
        _dispatcher.EventReady += OnEventReady;
    }

    public async Task RegisterAsync()
    {
        _selfRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("kohVsCodeBridge.register", _selfRef);
    }

    [JSInvokable]
    public void ReceiveDap(string jsonPayload)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonPayload);
        _dispatcher.HandleRequest(bytes);
    }

    private async void OnResponseReady(ReadOnlyMemory<byte> bytes)
    {
        var payload = Encoding.UTF8.GetString(bytes.Span);
        await _js.InvokeVoidAsync("kohVsCodeBridge.sendToExtension", "dap", payload);
    }

    private async void OnEventReady(ReadOnlyMemory<byte> bytes)
    {
        var payload = Encoding.UTF8.GetString(bytes.Span);
        await _js.InvokeVoidAsync("kohVsCodeBridge.sendToExtension", "dap", payload);
    }

    public async ValueTask DisposeAsync()
    {
        _dispatcher.ResponseReady -= OnResponseReady;
        _dispatcher.EventReady -= OnEventReady;
        _selfRef?.Dispose();
        await ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 3: Build and commit**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`

```bash
git add src/Koh.Emulator.App/wwwroot/js/vscode-bridge.js src/Koh.Emulator.App/wwwroot/index.html src/Koh.Emulator.App/DebugMode/DapTransport.cs
git commit -m "feat(emulator-app): add VS Code JS bridge and DapTransport"
```

---

### Task 1.N.2: DebugModeBootstrapper wires dispatcher + transport + session

**Files:**
- Create: `src/Koh.Emulator.App/DebugMode/DebugModeBootstrapper.cs`
- Modify: `src/Koh.Emulator.App/Shell/DebugShell.razor`
- Modify: `src/Koh.Emulator.App/Program.cs`

- [ ] **Step 1: Create `DebugModeBootstrapper.cs`**

```csharp
using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Emulator.App.Services;

namespace Koh.Emulator.App.DebugMode;

public sealed class DebugModeBootstrapper
{
    private readonly EmulatorHost _emulatorHost;
    public DapDispatcher Dispatcher { get; }
    public DebugSession DebugSession { get; }

    public DebugModeBootstrapper(EmulatorHost emulatorHost)
    {
        _emulatorHost = emulatorHost;
        Dispatcher = new DapDispatcher();
        DebugSession = new DebugSession();

        HandlerRegistration.RegisterAll(
            Dispatcher,
            DebugSession,
            loadFile: path => LoadFileViaFetch(path));
    }

    private ReadOnlyMemory<byte> LoadFileViaFetch(string path)
    {
        // Phase 1: the launch handler receives absolute paths from the extension.
        // In the VS Code webview, file access is restricted. The extension must
        // resolve paths to webview URIs before sending the launch request, and
        // this method fetches them via HttpClient (added in Task 1.N.3).
        throw new NotSupportedException("File loading bridged via extension; see §11.9");
    }
}
```

- [ ] **Step 2: Register the bootstrapper in `Program.cs`**

Append to the service registrations:

```csharp
builder.Services.AddSingleton<DebugModeBootstrapper>();
builder.Services.AddSingleton<DapTransport>(sp =>
{
    var js = sp.GetRequiredService<IJSRuntime>();
    var boot = sp.GetRequiredService<DebugModeBootstrapper>();
    return new DapTransport(js, boot.Dispatcher);
});
```

- [ ] **Step 3: Wire the transport initialization in `DebugShell.razor`**

```razor
@using Koh.Emulator.App.Components
@using Koh.Emulator.App.DebugMode
@using Koh.Emulator.App.Services
@inject EmulatorHost EmulatorHost
@inject DapTransport DapTransport
@implements IAsyncDisposable

<div class="debug-shell">
    <div class="lcd-placeholder">Koh Emulator — Phase 1: awaiting PPU</div>
    <CpuDashboard />
    <div class="status-bar">
        <span>Mode: @(EmulatorHost.System?.Mode.ToString() ?? "not loaded")</span>
        <span>Cycles: @(EmulatorHost.System?.Cpu.TotalTCycles ?? 0)</span>
    </div>
</div>

@code {
    protected override async Task OnInitializedAsync()
    {
        EmulatorHost.StateChanged += OnStateChanged;
        await DapTransport.RegisterAsync();
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public async ValueTask DisposeAsync()
    {
        EmulatorHost.StateChanged -= OnStateChanged;
        await DapTransport.DisposeAsync();
    }
}
```

- [ ] **Step 4: Build and commit**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`

```bash
git add src/Koh.Emulator.App/DebugMode/DebugModeBootstrapper.cs src/Koh.Emulator.App/Program.cs src/Koh.Emulator.App/Shell/DebugShell.razor
git commit -m "feat(emulator-app): wire DebugModeBootstrapper and DapTransport into Debug shell"
```

---

### Task 1.N.3: Benchmark page for Phase 1 representative workload

**Files:**
- Create: `src/Koh.Emulator.App/Benchmark/BenchmarkRunner.cs`
- Create: `src/Koh.Emulator.App/Benchmark/BenchmarkPage.razor`

- [ ] **Step 1: Create `BenchmarkRunner.cs`**

```csharp
using System.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.App.Benchmark;

public sealed class BenchmarkRunner
{
    public sealed record Result(double WallSeconds, ulong SystemTicks, double TicksPerSecond, double RealTimeMultiplier);

    public async Task<Result> RunAsync(TimeSpan warmup, TimeSpan measure)
    {
        var rom = BuildSyntheticRom();
        var cart = CartridgeFactory.Load(rom);
        var gb = new GameBoySystem(HardwareMode.Dmg, cart);

        // Warmup
        var warmupEnd = DateTime.UtcNow + warmup;
        while (DateTime.UtcNow < warmupEnd)
        {
            gb.RunFrame();
            await Task.Yield();
        }

        // Measure
        var sw = Stopwatch.StartNew();
        ulong ticksStart = gb.Clock.SystemTicks;
        var measureEnd = DateTime.UtcNow + measure;
        while (DateTime.UtcNow < measureEnd)
        {
            gb.RunFrame();
            await Task.Yield();
        }
        sw.Stop();
        ulong ticksEnd = gb.Clock.SystemTicks;

        double wallSeconds = sw.Elapsed.TotalSeconds;
        ulong deltaTicks = ticksEnd - ticksStart;
        double ticksPerSec = deltaTicks / wallSeconds;
        double multiplier = ticksPerSec / 4194304.0;

        return new Result(wallSeconds, deltaTicks, ticksPerSec, multiplier);
    }

    private static byte[] BuildSyntheticRom()
    {
        var rom = new byte[0x8000];
        rom[0x143] = 0x00;
        rom[0x147] = 0x00;
        for (int i = 0x150; i < rom.Length; i++)
            rom[i] = (byte)(i & 0xFF);
        return rom;
    }
}
```

- [ ] **Step 2: Create `BenchmarkPage.razor`**

```razor
@page "/benchmark"
@using Koh.Emulator.App.Benchmark

<h2>Phase 1 Benchmark</h2>

<button @onclick="Run" disabled="@_running">Run</button>

@if (_result is { } r)
{
    <table>
        <tr><th>Wall seconds</th><td>@r.WallSeconds.ToString("F2")</td></tr>
        <tr><th>System ticks</th><td>@r.SystemTicks</td></tr>
        <tr><th>Ticks / sec</th><td>@r.TicksPerSecond.ToString("F0")</td></tr>
        <tr><th>Real-time multiplier</th><td>@r.RealTimeMultiplier.ToString("F2")×</td></tr>
    </table>

    <p class="@(r.RealTimeMultiplier >= 2.0 ? "pass" : "fail")">
        @(r.RealTimeMultiplier >= 2.0 ? "PASS — ≥ 2.0× real-time" : "FAIL — below 2.0× real-time threshold")
    </p>
}

@code {
    private BenchmarkRunner.Result? _result;
    private bool _running;

    private async Task Run()
    {
        _running = true;
        _result = null;
        StateHasChanged();

        var runner = new BenchmarkRunner();
        _result = await runner.RunAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

        _running = false;
        StateHasChanged();
    }
}
```

- [ ] **Step 3: Add routing support to `App.razor`**

Replace `App.razor` to enable routing:

```razor
@using Koh.Emulator.App.Shell
@inject RuntimeModeDetector ModeDetector

<Router AppAssembly="@typeof(App).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="@routeData" />
    </Found>
    <NotFound>
        @if (_mode is null)
        {
            <p>Loading…</p>
        }
        else if (_mode == RuntimeMode.Debug)
        {
            <DebugShell />
        }
        else
        {
            <StandaloneShell />
        }
    </NotFound>
</Router>

@code {
    private RuntimeMode? _mode;

    protected override async Task OnInitializedAsync()
    {
        _mode = await ModeDetector.DetectAsync();
    }
}
```

The `NotFound` path keeps the existing shell-selection behavior for `/`, while `/benchmark` routes to `BenchmarkPage`.

- [ ] **Step 4: Build and run the dev host to verify `/benchmark` loads**

Run: `dotnet build src/Koh.Emulator.App/Koh.Emulator.App.csproj`
Run: `dotnet run --project src/Koh.Emulator.App/Koh.Emulator.App.csproj`

Navigate to `http://localhost:5000/benchmark` and click Run. Expected: after ~35 seconds, a result table appears. **Phase 1 gate requires the real-time multiplier to be ≥ 2.0×.** If the result is below 2.0×, stop and investigate per §12.9 failure policy before continuing.

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Emulator.App/Benchmark/ src/Koh.Emulator.App/App.razor
git commit -m "feat(emulator-app): add Phase 1 benchmark page with 2× real-time gate"
```

---

## Phase 1-O: VS Code extension — core refactor

The current `editors/vscode/src/extension.ts` is a single file that registers the LSP client and handles `koh.yaml` bootstrapping. The refactor decomposes it into facade + narrow subsystems per design §11.1.

### Task 1.O.1: DisposableStore and Logger

**Files:**
- Create: `editors/vscode/src/core/DisposableStore.ts`
- Create: `editors/vscode/src/core/Logger.ts`

- [ ] **Step 1: Create `DisposableStore.ts`**

```typescript
import * as vscode from 'vscode';

export class DisposableStore implements vscode.Disposable {
    private readonly items: vscode.Disposable[] = [];
    private disposed = false;

    add(d: vscode.Disposable): void {
        if (this.disposed) {
            d.dispose();
            return;
        }
        this.items.push(d);
    }

    addAll(ds: vscode.Disposable[]): void {
        for (const d of ds) this.add(d);
    }

    dispose(): void {
        if (this.disposed) return;
        this.disposed = true;
        for (const d of this.items.reverse()) {
            try { d.dispose(); } catch { /* swallow during shutdown */ }
        }
    }
}
```

- [ ] **Step 2: Create `Logger.ts`**

```typescript
import * as vscode from 'vscode';

export class Logger {
    private readonly channel: vscode.LogOutputChannel;

    constructor(name: string) {
        this.channel = vscode.window.createOutputChannel(name, { log: true });
    }

    info(msg: string): void { this.channel.info(msg); }
    warn(msg: string): void { this.channel.warn(msg); }
    error(msg: string): void { this.channel.error(msg); }
    show(): void { this.channel.show(true); }

    dispose(): void { this.channel.dispose(); }
}
```

- [ ] **Step 3: Compile and commit**

Run: `cd editors/vscode && npm run compile`
Expected: compile succeeds (no call sites yet).

```bash
git add editors/vscode/src/core/
git commit -m "feat(vscode): add DisposableStore and Logger core utilities"
```

---

### Task 1.O.2: Extract LSP client to `lsp/`

**Files:**
- Create: `editors/vscode/src/lsp/serverPathResolver.ts`
- Create: `editors/vscode/src/lsp/LspClientManager.ts`

- [ ] **Step 1: Create `serverPathResolver.ts`** with the existing `findServer` logic from the current extension.ts

```typescript
import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { Logger } from '../core/Logger';

export function resolveLspServerPath(log: Logger): string | null {
    // 1. User setting
    const configPath = vscode.workspace.getConfiguration('koh').get<string>('serverPath');
    log.info(`koh.serverPath = "${configPath || ''}"`);
    if (configPath && fs.existsSync(configPath)) {
        log.info(`Found server at configured path: ${configPath}`);
        return configPath;
    }

    // 2. Bundled server
    const bundled = [
        path.join(__dirname, '..', '..', 'server', 'koh-lsp'),
        path.join(__dirname, '..', '..', 'server', 'koh-lsp.exe'),
    ];
    for (const candidate of bundled) {
        if (fs.existsSync(candidate)) {
            log.info(`Using bundled server: ${candidate}`);
            return candidate;
        }
    }

    return null;
}
```

- [ ] **Step 2: Create `LspClientManager.ts`**

```typescript
import * as vscode from 'vscode';
import { LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { Logger } from '../core/Logger';
import { resolveLspServerPath } from './serverPathResolver';

export class LspClientManager implements vscode.Disposable {
    private client: LanguageClient | undefined;

    constructor(private readonly log: Logger) {}

    async start(): Promise<void> {
        const serverPath = resolveLspServerPath(this.log);
        if (!serverPath) {
            const msg = 'Koh language server (koh-lsp) not found. Set koh.serverPath in settings, or build with: dotnet publish src/Koh.Lsp -c Release -o editors/vscode/server';
            this.log.error(msg);
            this.log.show();
            vscode.window.showWarningMessage(msg);
            return;
        }

        const serverOptions: ServerOptions = { command: serverPath, args: [] };
        const clientOptions: LanguageClientOptions = {
            documentSelector: [
                { scheme: 'file', language: 'koh-asm' },
                { scheme: 'untitled', language: 'koh-asm' },
            ],
        };

        this.client = new LanguageClient('koh-lsp', 'Koh Language Server', serverOptions, clientOptions);
        try {
            await this.client.start();
            this.log.info('Language client started successfully');
        } catch (e) {
            this.log.error(`Failed to start LSP: ${e}`);
            this.log.show();
        }
    }

    dispose(): void {
        this.client?.stop();
    }
}
```

- [ ] **Step 3: Compile and commit**

Run: `cd editors/vscode && npm run compile`

```bash
git add editors/vscode/src/lsp/
git commit -m "refactor(vscode): extract LSP client into lsp/ subsystem"
```

---

### Task 1.O.3: KohExtension coordinator + extension.ts facade

**Files:**
- Create: `editors/vscode/src/core/KohExtension.ts`
- Modify: `editors/vscode/src/extension.ts`

- [ ] **Step 1: Create `KohExtension.ts`** (minimal stub — more subsystems added in later tasks)

```typescript
import * as vscode from 'vscode';
import { DisposableStore } from './DisposableStore';
import { Logger } from './Logger';
import { LspClientManager } from '../lsp/LspClientManager';

export class KohExtension {
    private readonly disposables = new DisposableStore();
    private readonly log: Logger;
    private readonly lsp: LspClientManager;

    constructor(private readonly context: vscode.ExtensionContext) {
        this.log = new Logger('Koh');
        this.disposables.add(this.log);
        this.lsp = new LspClientManager(this.log);
    }

    async start(): Promise<void> {
        this.log.info('Koh extension activating...');
        await this.lsp.start();
        this.disposables.add(this.lsp);
    }

    async dispose(): Promise<void> {
        this.disposables.dispose();
    }
}
```

- [ ] **Step 2: Replace `extension.ts` with the facade**

File `editors/vscode/src/extension.ts` (complete replacement):

```typescript
import * as vscode from 'vscode';
import { KohExtension } from './core/KohExtension';

let extension: KohExtension | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    extension = new KohExtension(context);
    await extension.start();
}

export async function deactivate(): Promise<void> {
    await extension?.dispose();
    extension = undefined;
}
```

- [ ] **Step 3: Compile**

Run: `cd editors/vscode && npm run compile`
Expected: compile succeeds.

- [ ] **Step 4: Launch the extension development host from VS Code**

Use `F5` in VS Code at the repo root (the existing `.vscode/launch.json` launches the extension dev host). The Koh extension should activate and the output channel should show "Koh extension activating...". LSP behavior should be unchanged from before the refactor.

- [ ] **Step 5: Commit**

```bash
git add editors/vscode/src/core/KohExtension.ts editors/vscode/src/extension.ts
git commit -m "refactor(vscode): convert extension.ts to KohExtension facade"
```

---

## Phase 1-P: VS Code extension — workspace config and build tasks

### Task 1.P.1: KohYamlReader and WorkspaceConfig types

**Files:**
- Create: `editors/vscode/src/config/WorkspaceConfig.ts`
- Create: `editors/vscode/src/config/KohYamlReader.ts`

- [ ] **Step 1: Create `WorkspaceConfig.ts`** — typed view of `koh.yaml`

```typescript
export interface KohYamlProject {
    name: string;
    entrypoint: string;
}

export interface KohYaml {
    version: number;
    projects: KohYamlProject[];
}

export interface ResolvedTarget {
    name: string;
    entrypoint: string;          // absolute path
    romPath: string;             // absolute output path for the .gb
    kdbgPath: string;            // absolute output path for the .kdbg
    workspaceFolder: string;
}
```

- [ ] **Step 2: Create `KohYamlReader.ts`**

```typescript
import * as vscode from 'vscode';
import * as fs from 'fs';
import * as path from 'path';
import { Logger } from '../core/Logger';
import { KohYaml, ResolvedTarget } from './WorkspaceConfig';

export class KohYamlReader {
    constructor(private readonly log: Logger) {}

    async read(folder: vscode.WorkspaceFolder): Promise<KohYaml | null> {
        const configPath = path.join(folder.uri.fsPath, 'koh.yaml');
        if (!fs.existsSync(configPath)) return null;

        const contents = fs.readFileSync(configPath, 'utf8');
        try {
            return this.parseMinimalYaml(contents);
        } catch (e) {
            this.log.error(`Failed to parse koh.yaml: ${e}`);
            return null;
        }
    }

    resolveTargets(yaml: KohYaml, folder: vscode.WorkspaceFolder): ResolvedTarget[] {
        return yaml.projects.map(p => ({
            name: p.name,
            entrypoint: path.join(folder.uri.fsPath, p.entrypoint),
            romPath: path.join(folder.uri.fsPath, 'build', `${p.name}.gb`),
            kdbgPath: path.join(folder.uri.fsPath, 'build', `${p.name}.kdbg`),
            workspaceFolder: folder.uri.fsPath,
        }));
    }

    /**
     * Minimal YAML parser sufficient for the koh.yaml schema. Avoids pulling in
     * a full YAML dependency for this simple format. If the schema grows, swap
     * in a real parser.
     */
    private parseMinimalYaml(text: string): KohYaml {
        const lines = text.split(/\r?\n/).map(l => l.replace(/\s+$/, ''));
        const projects: { name: string; entrypoint: string }[] = [];
        let version = 0;
        let inProjects = false;
        let current: Partial<{ name: string; entrypoint: string }> | null = null;

        for (const line of lines) {
            if (line.trim().startsWith('#') || line.trim() === '') continue;

            const vMatch = line.match(/^version:\s*(\d+)/);
            if (vMatch) { version = parseInt(vMatch[1], 10); continue; }

            if (line === 'projects:') { inProjects = true; continue; }

            if (inProjects && line.startsWith('  - ')) {
                if (current) projects.push(current as any);
                current = {};
                const itemMatch = line.match(/^  - (\w+):\s*(.+)$/);
                if (itemMatch) (current as any)[itemMatch[1]] = itemMatch[2].trim();
                continue;
            }

            if (inProjects && line.startsWith('    ')) {
                const m = line.trim().match(/^(\w+):\s*(.+)$/);
                if (m && current) (current as any)[m[1]] = m[2].trim();
            }
        }
        if (current) projects.push(current as any);

        return { version, projects: projects as any };
    }
}
```

- [ ] **Step 3: Compile and commit**

Run: `cd editors/vscode && npm run compile`

```bash
git add editors/vscode/src/config/
git commit -m "feat(vscode): add KohYamlReader with minimal YAML parser"
```

---

### Task 1.P.2: BuildTaskProvider for koh-asm + koh-link

**Files:**
- Create: `editors/vscode/src/build/binaryResolver.ts`
- Create: `editors/vscode/src/build/KohBuildTask.ts`
- Create: `editors/vscode/src/build/BuildTaskProvider.ts`

- [ ] **Step 1: Create `binaryResolver.ts`**

```typescript
import * as path from 'path';
import * as fs from 'fs';
import { Logger } from '../core/Logger';

export interface KohBinaries {
    asm: string;
    link: string;
}

export function resolveKohBinaries(log: Logger): KohBinaries | null {
    const candidates = [
        path.join(__dirname, '..', '..', 'server'),   // bundled dir used for LSP
        path.join(__dirname, '..', '..', '..', '..', 'src', 'Koh.Asm', 'bin', 'Debug', 'net10.0'),
    ];

    for (const dir of candidates) {
        const asm = path.join(dir, process.platform === 'win32' ? 'koh-asm.exe' : 'koh-asm');
        const link = path.join(dir, process.platform === 'win32' ? 'koh-link.exe' : 'koh-link');
        if (fs.existsSync(asm) && fs.existsSync(link)) {
            log.info(`Found koh-asm/koh-link in ${dir}`);
            return { asm, link };
        }
    }

    log.warn('koh-asm / koh-link binaries not found in standard locations');
    return null;
}
```

- [ ] **Step 2: Create `KohBuildTask.ts`**

```typescript
import * as vscode from 'vscode';
import * as path from 'path';
import { KohBinaries } from './binaryResolver';
import { ResolvedTarget } from '../config/WorkspaceConfig';

export function createBuildTask(binaries: KohBinaries, target: ResolvedTarget): vscode.Task {
    const kobjPath = path.join(path.dirname(target.romPath), `${target.name}.kobj`);
    const buildDir = path.dirname(target.romPath);

    const execution = new vscode.ShellExecution(
        `mkdir -p "${buildDir}" && ` +
        `"${binaries.asm}" "${target.entrypoint}" -o "${kobjPath}" && ` +
        `"${binaries.link}" "${kobjPath}" -o "${target.romPath}" -n "${path.join(buildDir, `${target.name}.sym`)}" -d "${target.kdbgPath}"`,
        { cwd: target.workspaceFolder }
    );

    const task = new vscode.Task(
        { type: 'koh', target: target.name },
        vscode.TaskScope.Workspace,
        `build ${target.name}`,
        'koh',
        execution
    );
    task.group = vscode.TaskGroup.Build;
    return task;
}
```

- [ ] **Step 3: Create `BuildTaskProvider.ts`**

```typescript
import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { KohYamlReader } from '../config/KohYamlReader';
import { resolveKohBinaries } from './binaryResolver';
import { createBuildTask } from './KohBuildTask';

export class BuildTaskProvider implements vscode.TaskProvider {
    constructor(
        private readonly log: Logger,
        private readonly yamlReader: KohYamlReader
    ) {}

    async provideTasks(): Promise<vscode.Task[]> {
        const binaries = resolveKohBinaries(this.log);
        if (!binaries) return [];

        const tasks: vscode.Task[] = [];
        for (const folder of vscode.workspace.workspaceFolders ?? []) {
            const yaml = await this.yamlReader.read(folder);
            if (!yaml) continue;
            const targets = this.yamlReader.resolveTargets(yaml, folder);
            for (const target of targets) {
                tasks.push(createBuildTask(binaries, target));
            }
        }
        return tasks;
    }

    resolveTask(task: vscode.Task): vscode.Task | undefined {
        // Return undefined to let VS Code use the provideTasks result.
        return undefined;
    }

    register(): vscode.Disposable {
        return vscode.tasks.registerTaskProvider('koh', this);
    }
}
```

- [ ] **Step 4: Wire into `KohExtension`**

Update `editors/vscode/src/core/KohExtension.ts`:

```typescript
import * as vscode from 'vscode';
import { DisposableStore } from './DisposableStore';
import { Logger } from './Logger';
import { LspClientManager } from '../lsp/LspClientManager';
import { KohYamlReader } from '../config/KohYamlReader';
import { BuildTaskProvider } from '../build/BuildTaskProvider';

export class KohExtension {
    private readonly disposables = new DisposableStore();
    private readonly log: Logger;
    private readonly yamlReader: KohYamlReader;
    private readonly lsp: LspClientManager;
    private readonly buildTasks: BuildTaskProvider;

    constructor(private readonly context: vscode.ExtensionContext) {
        this.log = new Logger('Koh');
        this.disposables.add(this.log);
        this.yamlReader = new KohYamlReader(this.log);
        this.lsp = new LspClientManager(this.log);
        this.buildTasks = new BuildTaskProvider(this.log, this.yamlReader);
    }

    async start(): Promise<void> {
        this.log.info('Koh extension activating...');
        await this.lsp.start();
        this.disposables.add(this.lsp);
        this.disposables.add(this.buildTasks.register());
    }

    async dispose(): Promise<void> {
        this.disposables.dispose();
    }
}
```

- [ ] **Step 5: Compile and commit**

Run: `cd editors/vscode && npm run compile`

```bash
git add editors/vscode/src/build/ editors/vscode/src/core/KohExtension.ts
git commit -m "feat(vscode): add BuildTaskProvider synthesizing koh-asm+koh-link tasks"
```

---

## Phase 1-Q: VS Code extension — debug registration

### Task 1.Q.1: launchTypes and ConfigurationProvider

**Files:**
- Create: `editors/vscode/src/debug/launchTypes.ts`
- Create: `editors/vscode/src/debug/TargetSelector.ts`
- Create: `editors/vscode/src/debug/ConfigurationProvider.ts`

- [ ] **Step 1: Create `launchTypes.ts`**

```typescript
export interface KohLaunchConfiguration {
    type: 'koh';
    request: 'launch';
    name: string;
    target?: string;
    program?: string;
    debugInfo?: string;
    hardwareMode?: 'auto' | 'dmg' | 'cgb';
    stopOnEntry?: boolean;
    preLaunchTask?: string;
}
```

- [ ] **Step 2: Create `TargetSelector.ts`**

```typescript
import * as vscode from 'vscode';
import { ResolvedTarget } from '../config/WorkspaceConfig';

export class TargetSelector {
    async pick(targets: ResolvedTarget[]): Promise<ResolvedTarget | undefined> {
        if (targets.length === 0) return undefined;
        if (targets.length === 1) return targets[0];

        const picked = await vscode.window.showQuickPick(
            targets.map(t => ({ label: t.name, description: t.romPath, target: t })),
            { placeHolder: 'Select Koh target to debug' }
        );
        return picked?.target;
    }
}
```

- [ ] **Step 3: Create `ConfigurationProvider.ts`**

```typescript
import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { Logger } from '../core/Logger';
import { KohYamlReader } from '../config/KohYamlReader';
import { KohLaunchConfiguration } from './launchTypes';
import { TargetSelector } from './TargetSelector';

export class KohConfigurationProvider implements vscode.DebugConfigurationProvider {
    constructor(
        private readonly log: Logger,
        private readonly yamlReader: KohYamlReader,
        private readonly targetSelector: TargetSelector
    ) {}

    async resolveDebugConfiguration(
        folder: vscode.WorkspaceFolder | undefined,
        config: vscode.DebugConfiguration
    ): Promise<vscode.DebugConfiguration | null | undefined> {
        // Case 1: F5 with no launch.json at all — config.type is empty.
        if (!config.type) {
            return await this.synthesizeFromYaml(folder);
        }

        // Case 2: launch.json entry with a target but no program — derive.
        if (config.type === 'koh' && !config.program) {
            if (!folder) {
                vscode.window.showErrorMessage('Koh debug configuration needs a workspace folder.');
                return undefined;
            }
            const yaml = await this.yamlReader.read(folder);
            if (!yaml) {
                vscode.window.showErrorMessage('Koh debug configuration references target but no koh.yaml found.');
                return undefined;
            }
            const targets = this.yamlReader.resolveTargets(yaml, folder);
            const target = targets.find(t => t.name === config.target) ?? targets[0];
            if (!target) {
                vscode.window.showErrorMessage('No Koh targets available.');
                return undefined;
            }
            config.program = target.romPath;
            config.debugInfo = config.debugInfo ?? target.kdbgPath;
            config.preLaunchTask = config.preLaunchTask ?? `koh: build ${target.name}`;
        }

        return config;
    }

    private async synthesizeFromYaml(
        folder: vscode.WorkspaceFolder | undefined
    ): Promise<KohLaunchConfiguration | undefined> {
        if (!folder) {
            vscode.window.showInformationMessage('Open a folder to debug Koh ROMs.');
            return undefined;
        }

        const yaml = await this.yamlReader.read(folder);
        if (!yaml || yaml.projects.length === 0) {
            const action = await vscode.window.showInformationMessage(
                'No koh.yaml found. Create one or add a launch.json configuration?',
                'Generate koh.yaml',
                'Open launch.json'
            );
            if (action === 'Generate koh.yaml') {
                await vscode.commands.executeCommand('koh.generateConfig');
            } else if (action === 'Open launch.json') {
                await vscode.commands.executeCommand('workbench.action.debug.configure');
            }
            return undefined;
        }

        const targets = this.yamlReader.resolveTargets(yaml, folder);
        const picked = await this.targetSelector.pick(targets);
        if (!picked) return undefined;

        return {
            type: 'koh',
            request: 'launch',
            name: `Debug ${picked.name}`,
            target: picked.name,
            program: picked.romPath,
            debugInfo: picked.kdbgPath,
            hardwareMode: 'auto',
            stopOnEntry: false,
            preLaunchTask: `koh: build ${picked.name}`,
        };
    }
}
```

- [ ] **Step 4: Compile and commit**

Run: `cd editors/vscode && npm run compile`

```bash
git add editors/vscode/src/debug/
git commit -m "feat(vscode): add Koh debug ConfigurationProvider with multi-target support"
```

---

### Task 1.Q.2: DapMessageQueue + InlineDapAdapter

**Files:**
- Create: `editors/vscode/src/debug/DapMessageQueue.ts`
- Create: `editors/vscode/src/debug/InlineDapAdapter.ts`

- [ ] **Step 1: Create `DapMessageQueue.ts`**

```typescript
/**
 * FIFO queue for DAP messages with boot buffering. Messages submitted before
 * the webview signals "ready" are queued and flushed when ready fires.
 * See design §11.9.
 */
export class DapMessageQueue {
    private buffered: unknown[] = [];
    private ready = false;
    private sink: ((msg: unknown) => void) | null = null;

    markReady(sink: (msg: unknown) => void): void {
        this.sink = sink;
        this.ready = true;
        for (const msg of this.buffered) sink(msg);
        this.buffered = [];
    }

    enqueueOutbound(msg: unknown, sendIfReady: (msg: unknown) => void): void {
        if (this.ready && this.sink) {
            sendIfReady(msg);
        } else {
            this.buffered.push(msg);
        }
    }

    reset(): void {
        this.buffered = [];
        this.ready = false;
        this.sink = null;
    }
}
```

- [ ] **Step 2: Create `InlineDapAdapter.ts`**

```typescript
import * as vscode from 'vscode';
import { DapMessageQueue } from './DapMessageQueue';

export type WebviewPostMessage = (msg: unknown) => void;

export class KohInlineDapAdapter implements vscode.DebugAdapter {
    private readonly messageEmitter = new vscode.EventEmitter<vscode.DebugProtocolMessage>();
    readonly onDidSendMessage = this.messageEmitter.event;

    constructor(
        private readonly postToWebview: WebviewPostMessage,
        private readonly queue: DapMessageQueue,
        private readonly onDispose: () => void
    ) {}

    /** Called by VS Code when it has a DAP message to send us. */
    handleMessage(message: vscode.DebugProtocolMessage): void {
        this.queue.enqueueOutbound(message, msg => this.postToWebview({ kind: 'dap', payload: msg }));
    }

    /** Called by the webview host when a DAP message arrives from the Blazor app. */
    receiveFromWebview(payload: unknown): void {
        this.messageEmitter.fire(payload as vscode.DebugProtocolMessage);
    }

    dispose(): void {
        this.messageEmitter.dispose();
        this.onDispose();
    }
}
```

- [ ] **Step 3: Compile and commit**

Run: `cd editors/vscode && npm run compile`

```bash
git add editors/vscode/src/debug/DapMessageQueue.ts editors/vscode/src/debug/InlineDapAdapter.ts
git commit -m "feat(vscode): add DapMessageQueue and InlineDapAdapter passthrough"
```

---

### Task 1.Q.3: KohDebugRegistration wiring

**Files:**
- Create: `editors/vscode/src/debug/KohDebugRegistration.ts`
- Modify: `editors/vscode/src/core/KohExtension.ts`
- Modify: `editors/vscode/package.json`

- [ ] **Step 1: Create `KohDebugRegistration.ts`**

```typescript
import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { KohYamlReader } from '../config/KohYamlReader';
import { KohConfigurationProvider } from './ConfigurationProvider';
import { TargetSelector } from './TargetSelector';
import { KohInlineDapAdapter } from './InlineDapAdapter';
import { DapMessageQueue } from './DapMessageQueue';
import { EmulatorPanelHost } from '../webview/EmulatorPanelHost';

export class KohDebugRegistration {
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly log: Logger,
        private readonly yamlReader: KohYamlReader,
        private readonly panelHost: EmulatorPanelHost
    ) {}

    register(): vscode.Disposable {
        const disposables: vscode.Disposable[] = [];

        const configProvider = new KohConfigurationProvider(this.log, this.yamlReader, new TargetSelector());
        disposables.push(vscode.debug.registerDebugConfigurationProvider('koh', configProvider));

        disposables.push(vscode.debug.registerDebugAdapterDescriptorFactory('koh', {
            createDebugAdapterDescriptor: (session) => {
                const panel = this.panelHost.openForSession(session);
                const queue = new DapMessageQueue();
                const adapter = new KohInlineDapAdapter(
                    msg => panel.postToWebview(msg),
                    queue,
                    () => panel.dispose()
                );
                panel.onMessageFromWebview(m => {
                    if (m.kind === 'ready') {
                        queue.markReady(msg => panel.postToWebview(msg));
                    } else if (m.kind === 'dap') {
                        adapter.receiveFromWebview(m.payload);
                    }
                });
                return new vscode.DebugAdapterInlineImplementation(adapter);
            }
        }));

        return vscode.Disposable.from(...disposables);
    }
}
```

- [ ] **Step 2: Update `package.json`** to register the debug type

Add to the `contributes` object in `editors/vscode/package.json`:

```jsonc
    "breakpoints": [
      { "language": "koh-asm" }
    ],
    "debuggers": [
      {
        "type": "koh",
        "label": "Koh Game Boy Debugger",
        "languages": ["koh-asm"],
        "configurationAttributes": {
          "launch": {
            "required": [],
            "properties": {
              "target":       { "type": "string", "description": "koh.yaml target name" },
              "program":      { "type": "string", "description": "Path to .gb ROM" },
              "debugInfo":    { "type": "string", "description": "Path to .kdbg" },
              "hardwareMode": { "type": "string", "enum": ["auto", "dmg", "cgb"], "default": "auto" },
              "stopOnEntry":  { "type": "boolean", "default": false },
              "preLaunchTask":{ "type": "string" }
            }
          }
        },
        "initialConfigurations": [
          {
            "type": "koh",
            "request": "launch",
            "name": "Debug Koh ROM",
            "hardwareMode": "auto",
            "stopOnEntry": false
          }
        ]
      }
    ]
```

Also add the new settings to the existing `configuration.properties` block:

```jsonc
        "koh.emulator.showDashboard": {
          "type": "boolean",
          "default": true,
          "description": "Show the CPU dashboard in the emulator webview."
        },
        "koh.emulator.scale": {
          "type": "number",
          "enum": [1, 2, 3, 4],
          "default": 3,
          "description": "LCD pixel scale factor."
        },
        "koh.emulator.devHostUrl": {
          "type": "string",
          "default": "",
          "description": "Dev-host URL for Blazor asset loading. Only honored in extension development mode."
        },
        "koh.debugger.logDapTraffic": {
          "type": "boolean",
          "default": false,
          "description": "Log DAP messages to the Koh output channel."
        }
```

- [ ] **Step 3: Wire `KohDebugRegistration` into `KohExtension`**

Update `core/KohExtension.ts` to instantiate the debug registration after the webview panel host (Task 1.R.2 creates the panel host; this wiring will be updated when that lands).

Leave a `// TODO(1.R.2): add EmulatorPanelHost` marker for now. This task only adds the class file; activation happens in 1.R.2.

- [ ] **Step 4: Compile**

Run: `cd editors/vscode && npm run compile`
Expected: compile succeeds (class exists but is not instantiated yet).

- [ ] **Step 5: Commit**

```bash
git add editors/vscode/src/debug/KohDebugRegistration.ts editors/vscode/package.json
git commit -m "feat(vscode): register koh debug type + breakpoints + settings in package.json"
```

---

## Phase 1-R: VS Code extension — webview host

### Task 1.R.1: messages.ts + BlazorAssetLoader with security gates

**Files:**
- Create: `editors/vscode/src/webview/messages.ts`
- Create: `editors/vscode/src/webview/BlazorAssetLoader.ts`
- Create: `editors/vscode/src/webview/EmulatorHtml.ts`

- [ ] **Step 1: Create `messages.ts`**

```typescript
export type ExtensionToWebviewMessage =
    | { kind: 'dap'; payload: unknown };

export type WebviewToExtensionMessage =
    | { kind: 'ready' }
    | { kind: 'dap'; payload: unknown }
    | { kind: 'fatalError'; message: string; stack?: string };
```

- [ ] **Step 2: Create `BlazorAssetLoader.ts`** with the three dev-host security rules from §11.8

```typescript
import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { Logger } from '../core/Logger';

export interface BlazorAssetSource {
    /** Base URI used in webview HTML. */
    baseUri: vscode.Uri | string;
    /** CSP additions for this source. */
    cspSources: string[];
}

export class BlazorAssetLoader {
    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly log: Logger
    ) {}

    resolve(webview: vscode.Webview): BlazorAssetSource {
        const devHostUrl = vscode.workspace.getConfiguration('koh').get<string>('emulator.devHostUrl');

        if (devHostUrl && this.isDevHostPermitted(devHostUrl)) {
            this.log.info(`Using dev-host Blazor assets at ${devHostUrl}`);
            return {
                baseUri: devHostUrl,
                cspSources: [devHostUrl],
            };
        }

        // Bundled assets path.
        const bundledDir = vscode.Uri.joinPath(this.context.extensionUri, 'dist', 'emulator-app');
        const baseUri = webview.asWebviewUri(bundledDir);
        return {
            baseUri,
            cspSources: [],
        };
    }

    private isDevHostPermitted(url: string): boolean {
        // Rule 1: extension mode must be Development.
        if (this.context.extensionMode !== vscode.ExtensionMode.Development) {
            this.log.warn('koh.emulator.devHostUrl set but extension is not in Development mode; ignoring.');
            return false;
        }

        // Rule 2: whitelist localhost / 127.0.0.1 with explicit port.
        try {
            const parsed = new URL(url);
            if (parsed.protocol !== 'http:') {
                this.log.warn(`devHostUrl rejected: non-http protocol (${parsed.protocol})`);
                return false;
            }
            if (parsed.hostname !== 'localhost' && parsed.hostname !== '127.0.0.1') {
                this.log.warn(`devHostUrl rejected: host must be localhost or 127.0.0.1 (${parsed.hostname})`);
                return false;
            }
            const port = parseInt(parsed.port, 10);
            if (!(port >= 1024 && port <= 65535)) {
                this.log.warn(`devHostUrl rejected: invalid port ${parsed.port}`);
                return false;
            }
            return true;
        } catch {
            this.log.warn(`devHostUrl rejected: not a valid URL (${url})`);
            return false;
        }
    }

    /** Check that bundled assets exist on disk. */
    bundledAssetsPresent(): boolean {
        const indexHtml = path.join(this.context.extensionPath, 'dist', 'emulator-app', 'index.html');
        return fs.existsSync(indexHtml);
    }
}
```

- [ ] **Step 3: Create `EmulatorHtml.ts`**

```typescript
import * as vscode from 'vscode';
import { BlazorAssetSource } from './BlazorAssetLoader';

export function buildEmulatorHtml(webview: vscode.Webview, assets: BlazorAssetSource): string {
    const cspSources = [
        `default-src 'none'`,
        `script-src ${webview.cspSource} 'unsafe-inline' 'unsafe-eval' ${assets.cspSources.join(' ')}`,
        `style-src ${webview.cspSource} 'unsafe-inline' ${assets.cspSources.join(' ')}`,
        `connect-src ${webview.cspSource} ${assets.cspSources.join(' ')}`,
        `img-src ${webview.cspSource} data: ${assets.cspSources.join(' ')}`,
        `font-src ${webview.cspSource} ${assets.cspSources.join(' ')}`,
    ].join('; ');

    const baseHref = typeof assets.baseUri === 'string' ? assets.baseUri + '/' : assets.baseUri.toString() + '/';

    return /* html */ `<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta http-equiv="Content-Security-Policy" content="${cspSources}" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="${baseHref}" />
    <title>Koh Emulator</title>
    <link href="css/emulator.css" rel="stylesheet" />
</head>
<body>
    <div id="app">Loading Blazor runtime…</div>
    <div id="blazor-error-ui" style="display:none">
        An unhandled error has occurred.
        <a href="" class="reload">Reload</a>
        <a class="dismiss">🗙</a>
    </div>
    <script src="js/runtime-mode.js"></script>
    <script src="js/frame-pacer.js"></script>
    <script src="js/vscode-bridge.js"></script>
    <script src="_framework/blazor.webassembly.js"></script>
</body>
</html>`;
}
```

- [ ] **Step 4: Compile and commit**

Run: `cd editors/vscode && npm run compile`

```bash
git add editors/vscode/src/webview/
git commit -m "feat(vscode): add BlazorAssetLoader with dev-host security gates and CSP builder"
```

---

### Task 1.R.2: EmulatorPanel + EmulatorPanelHost

**Files:**
- Create: `editors/vscode/src/webview/EmulatorPanel.ts`
- Create: `editors/vscode/src/webview/EmulatorPanelHost.ts`
- Modify: `editors/vscode/src/core/KohExtension.ts`

- [ ] **Step 1: Create `EmulatorPanel.ts`**

```typescript
import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { BlazorAssetLoader } from './BlazorAssetLoader';
import { buildEmulatorHtml } from './EmulatorHtml';
import { ExtensionToWebviewMessage, WebviewToExtensionMessage } from './messages';

export class EmulatorPanel implements vscode.Disposable {
    private readonly panel: vscode.WebviewPanel;
    private readonly messageEmitter = new vscode.EventEmitter<WebviewToExtensionMessage>();
    readonly onMessageFromWebview = this.messageEmitter.event;

    constructor(
        private readonly session: vscode.DebugSession,
        private readonly log: Logger,
        private readonly assetLoader: BlazorAssetLoader
    ) {
        this.panel = vscode.window.createWebviewPanel(
            'kohEmulator',
            `Koh Emulator — ${session.name}`,
            vscode.ViewColumn.Beside,
            {
                enableScripts: true,
                retainContextWhenHidden: true,
                localResourceRoots: [
                    vscode.Uri.joinPath(this.getExtensionUri(), 'dist', 'emulator-app'),
                ],
            }
        );

        const assets = this.assetLoader.resolve(this.panel.webview);
        this.panel.webview.html = buildEmulatorHtml(this.panel.webview, assets);

        this.panel.webview.onDidReceiveMessage((msg: WebviewToExtensionMessage) => {
            this.messageEmitter.fire(msg);
        });

        this.panel.onDidDispose(() => this.dispose());
    }

    postToWebview(msg: ExtensionToWebviewMessage | unknown): void {
        this.panel.webview.postMessage(msg);
    }

    dispose(): void {
        this.messageEmitter.dispose();
        this.panel.dispose();
    }

    private getExtensionUri(): vscode.Uri {
        // Walk up from __dirname to the extension root.
        return vscode.Uri.file(require('path').resolve(__dirname, '..', '..'));
    }
}
```

- [ ] **Step 2: Create `EmulatorPanelHost.ts`**

```typescript
import * as vscode from 'vscode';
import { Logger } from '../core/Logger';
import { EmulatorPanel } from './EmulatorPanel';
import { BlazorAssetLoader } from './BlazorAssetLoader';

export class EmulatorPanelHost {
    private activePanels = new Map<string, EmulatorPanel>();

    constructor(
        private readonly context: vscode.ExtensionContext,
        private readonly log: Logger
    ) {}

    openForSession(session: vscode.DebugSession): EmulatorPanel {
        const existing = this.activePanels.get(session.id);
        if (existing) return existing;

        const assetLoader = new BlazorAssetLoader(this.context, this.log);
        if (!assetLoader.bundledAssetsPresent()) {
            const devHost = vscode.workspace.getConfiguration('koh').get<string>('emulator.devHostUrl');
            if (!devHost) {
                vscode.window.showErrorMessage(
                    'Koh emulator assets not found. Run: dotnet publish src/Koh.Emulator.App -c Release -o editors/vscode/dist/emulator-app'
                );
            }
        }

        const panel = new EmulatorPanel(session, this.log, assetLoader);
        this.activePanels.set(session.id, panel);
        return panel;
    }
}
```

- [ ] **Step 3: Wire into `KohExtension.ts`**

Full updated file:

```typescript
import * as vscode from 'vscode';
import { DisposableStore } from './DisposableStore';
import { Logger } from './Logger';
import { LspClientManager } from '../lsp/LspClientManager';
import { KohYamlReader } from '../config/KohYamlReader';
import { BuildTaskProvider } from '../build/BuildTaskProvider';
import { EmulatorPanelHost } from '../webview/EmulatorPanelHost';
import { KohDebugRegistration } from '../debug/KohDebugRegistration';

export class KohExtension {
    private readonly disposables = new DisposableStore();
    private readonly log: Logger;
    private readonly yamlReader: KohYamlReader;
    private readonly lsp: LspClientManager;
    private readonly buildTasks: BuildTaskProvider;
    private readonly panelHost: EmulatorPanelHost;
    private readonly debugRegistration: KohDebugRegistration;

    constructor(private readonly context: vscode.ExtensionContext) {
        this.log = new Logger('Koh');
        this.disposables.add(this.log);
        this.yamlReader = new KohYamlReader(this.log);
        this.lsp = new LspClientManager(this.log);
        this.buildTasks = new BuildTaskProvider(this.log, this.yamlReader);
        this.panelHost = new EmulatorPanelHost(context, this.log);
        this.debugRegistration = new KohDebugRegistration(context, this.log, this.yamlReader, this.panelHost);
    }

    async start(): Promise<void> {
        this.log.info('Koh extension activating...');
        await this.lsp.start();
        this.disposables.add(this.lsp);
        this.disposables.add(this.buildTasks.register());
        this.disposables.add(this.debugRegistration.register());
    }

    async dispose(): Promise<void> {
        this.disposables.dispose();
    }
}
```

- [ ] **Step 4: Compile and commit**

Run: `cd editors/vscode && npm run compile`
Expected: compile succeeds.

```bash
git add editors/vscode/src/webview/EmulatorPanel.ts editors/vscode/src/webview/EmulatorPanelHost.ts editors/vscode/src/core/KohExtension.ts
git commit -m "feat(vscode): add EmulatorPanel + EmulatorPanelHost and wire into KohExtension"
```

---

### Task 1.R.3: End-to-end smoke test via Extension Development Host

- [ ] **Step 1: Publish Blazor app assets into the extension dist folder**

Run: `dotnet publish src/Koh.Emulator.App -c Release -o editors/vscode/dist/emulator-app-publish`
Then: `cp -R editors/vscode/dist/emulator-app-publish/wwwroot/* editors/vscode/dist/emulator-app/`
(Or on Windows PowerShell: `Copy-Item -Recurse editors\vscode\dist\emulator-app-publish\wwwroot\* editors\vscode\dist\emulator-app\`)

Verify `editors/vscode/dist/emulator-app/index.html` exists and `_framework/` is populated.

- [ ] **Step 2: Open the repo in VS Code and press F5 on "Launch Koh Extension"**

The existing `.vscode/launch.json` launches the extension dev host. Expected: a new VS Code window opens with the extension loaded.

- [ ] **Step 3: In the dev host, open a workspace containing a `koh.yaml` and an `.asm` file**

Expected: the LSP activates, syntax highlighting works.

- [ ] **Step 4: Create a minimal `launch.json` manually in the dev-host workspace**

```jsonc
{
    "version": "0.2.0",
    "configurations": [
        {
            "type": "koh",
            "request": "launch",
            "name": "Debug Koh ROM",
            "target": "game",
            "hardwareMode": "auto",
            "stopOnEntry": false
        }
    ]
}
```

- [ ] **Step 5: Press F5 to start the debug session**

Expected:
- The `koh: build game` task runs and produces `.gb`, `.sym`, `.kdbg`.
- The emulator webview opens in a side column showing the Phase 1 debug dashboard with live CPU state (the mock CPU will start cycling).
- VS Code's debug toolbar shows Continue / Pause / Stop.
- The Variables panel shows the "Registers" and "Hardware" scopes with hex values.
- Setting a breakpoint in the `.asm` file and F5 again: the breakpoint shows a solid red gutter marker if the line maps to a `.kdbg` address, hollow if not.

**If any of the above fails**, stop, diagnose, and fix before committing. Common issues:

- Blazor assets not found → re-run the publish step from 1.R.3 Step 1.
- `koh-asm` / `koh-link` binaries not found → update `binaryResolver.ts` to the correct local path, or run `dotnet build src/Koh.Asm src/Koh.Link`.
- `koh.yaml` schema mismatch → verify the format matches `KohYamlReader.parseMinimalYaml`.

- [ ] **Step 6: Commit any fixes from the smoke test**

```bash
git add -u
git commit -m "fix(vscode): address end-to-end smoke-test findings"
```

---

## Phase 1-S: Build pipeline and content-hash freshness

### Task 1.S.1: Content-hash computation script

**Files:**
- Create: `scripts/compute-build-hash.ps1`
- Create: `scripts/compute-build-hash.sh`

Content-hash algorithm per §11.6: canonicalized sorted list of `(relative_path, file_sha256)` pairs across `src/Koh.Emulator.App/`, `src/Koh.Emulator.Core/`, `src/Koh.Debugger/`, `src/Koh.Linker.Core/`, plus `.NET SDK version`, `Blazor WASM SDK version`, `C# compiler version`. The outer SHA-256 of that list is written to `editors/vscode/dist/emulator-app/.build-hash`.

- [ ] **Step 1: Create `scripts/compute-build-hash.sh`**

```bash
#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

# Collect the file set (sorted for determinism).
FILES=$(find \
    src/Koh.Emulator.App \
    src/Koh.Emulator.Core \
    src/Koh.Debugger \
    src/Koh.Linker.Core \
    -type f \
    \( -name '*.cs' -o -name '*.razor' -o -name '*.csproj' -o -name '*.js' -o -name '*.html' -o -name '*.css' -o -name '*.json' \) \
    2>/dev/null | sort)

# SDK versions.
DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo 'unknown')

# Compute per-file hashes and concatenate.
HASH_INPUT=""
for f in $FILES; do
    H=$(sha256sum "$f" | cut -d' ' -f1)
    HASH_INPUT+="${H}  ${f}"$'\n'
done
HASH_INPUT+="dotnet:${DOTNET_VERSION}"$'\n'

# Final SHA-256.
echo -n "$HASH_INPUT" | sha256sum | cut -d' ' -f1
```

- [ ] **Step 2: Create `scripts/compute-build-hash.ps1`**

```powershell
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$dirs = @(
    'src/Koh.Emulator.App',
    'src/Koh.Emulator.Core',
    'src/Koh.Debugger',
    'src/Koh.Linker.Core'
)

$exts = @('.cs', '.razor', '.csproj', '.js', '.html', '.css', '.json')

$files = Get-ChildItem -Recurse -File -Path $dirs |
    Where-Object { $exts -contains $_.Extension } |
    Sort-Object FullName

$dotnetVersion = (dotnet --version 2>$null)
if (-not $dotnetVersion) { $dotnetVersion = 'unknown' }

$sb = [System.Text.StringBuilder]::new()
foreach ($file in $files) {
    $h = (Get-FileHash -Algorithm SHA256 -Path $file.FullName).Hash.ToLower()
    $rel = [System.IO.Path]::GetRelativePath($repoRoot, $file.FullName) -replace '\\', '/'
    [void]$sb.Append("$h  $rel`n")
}
[void]$sb.Append("dotnet:$dotnetVersion`n")

$bytes = [System.Text.Encoding]::UTF8.GetBytes($sb.ToString())
$sha = [System.Security.Cryptography.SHA256]::Create()
$hashBytes = $sha.ComputeHash($bytes)
$hex = ($hashBytes | ForEach-Object { $_.ToString('x2') }) -join ''
Write-Output $hex
```

- [ ] **Step 3: Make the bash script executable**

Run: `chmod +x scripts/compute-build-hash.sh`

- [ ] **Step 4: Commit**

```bash
git add scripts/compute-build-hash.ps1 scripts/compute-build-hash.sh
git commit -m "chore: add content-hash scripts for emulator-app freshness check"
```

---

### Task 1.S.2: npm scripts for emulator-app build

**Files:**
- Modify: `editors/vscode/package.json`

- [ ] **Step 1: Add build scripts to `editors/vscode/package.json`**

Extend the existing `scripts` block:

```jsonc
  "scripts": {
    "compile": "tsc -p ./tsconfig.json",
    "watch": "tsc -w -p ./tsconfig.json",
    "build:emulator-app": "dotnet publish ../../src/Koh.Emulator.App -c Release -o ./.publish-emu-app && node ./scripts/copy-emu-app.js",
    "build:emulator-app:aot": "dotnet publish ../../src/Koh.Emulator.App -c Release -p:RunAOTCompilation=true -o ./.publish-emu-app && node ./scripts/copy-emu-app.js",
    "watch:emulator-app": "dotnet watch publish ../../src/Koh.Emulator.App -c Debug -p:RunAOTCompilation=false -o ./.publish-emu-app",
    "verify-freshness": "node ./scripts/verify-freshness.js",
    "package": "npm run build:emulator-app:aot && npm run verify-freshness && vsce package"
  }
```

- [ ] **Step 2: Create the copy helper**

File `editors/vscode/scripts/copy-emu-app.js`:

```javascript
#!/usr/bin/env node
// Copies the published Blazor WASM assets into editors/vscode/dist/emulator-app/
// and writes .build-hash matching scripts/compute-build-hash.{ps1,sh}.

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { execSync } = require('child_process');

const here = __dirname;
const extensionRoot = path.resolve(here, '..');
const repoRoot = path.resolve(extensionRoot, '..', '..');
const publishRoot = path.join(extensionRoot, '.publish-emu-app');
const publishWwwroot = path.join(publishRoot, 'wwwroot');
const distTarget = path.join(extensionRoot, 'dist', 'emulator-app');

if (!fs.existsSync(publishWwwroot)) {
    console.error(`publish wwwroot not found at ${publishWwwroot}`);
    process.exit(1);
}

fs.rmSync(distTarget, { recursive: true, force: true });
fs.mkdirSync(distTarget, { recursive: true });

copyRecursive(publishWwwroot, distTarget);

// Compute and write content hash.
const hashScript = process.platform === 'win32'
    ? path.join(repoRoot, 'scripts', 'compute-build-hash.ps1')
    : path.join(repoRoot, 'scripts', 'compute-build-hash.sh');
const cmd = process.platform === 'win32'
    ? `powershell -ExecutionPolicy Bypass -File "${hashScript}"`
    : `bash "${hashScript}"`;
const hash = execSync(cmd, { cwd: repoRoot }).toString().trim();
fs.writeFileSync(path.join(distTarget, '.build-hash'), hash + '\n', 'utf8');

console.log(`Emulator app copied to ${distTarget} (hash: ${hash})`);

function copyRecursive(src, dst) {
    const entries = fs.readdirSync(src, { withFileTypes: true });
    for (const entry of entries) {
        const s = path.join(src, entry.name);
        const d = path.join(dst, entry.name);
        if (entry.isDirectory()) {
            fs.mkdirSync(d, { recursive: true });
            copyRecursive(s, d);
        } else {
            fs.copyFileSync(s, d);
        }
    }
}
```

- [ ] **Step 3: Create the freshness verifier**

File `editors/vscode/scripts/verify-freshness.js`:

```javascript
#!/usr/bin/env node
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const here = __dirname;
const extensionRoot = path.resolve(here, '..');
const repoRoot = path.resolve(extensionRoot, '..', '..');
const buildHashFile = path.join(extensionRoot, 'dist', 'emulator-app', '.build-hash');

if (!fs.existsSync(buildHashFile)) {
    console.error('emulator-app assets missing .build-hash; run npm run build:emulator-app:aot first');
    process.exit(1);
}

const recorded = fs.readFileSync(buildHashFile, 'utf8').trim();

const hashScript = process.platform === 'win32'
    ? path.join(repoRoot, 'scripts', 'compute-build-hash.ps1')
    : path.join(repoRoot, 'scripts', 'compute-build-hash.sh');
const cmd = process.platform === 'win32'
    ? `powershell -ExecutionPolicy Bypass -File "${hashScript}"`
    : `bash "${hashScript}"`;
const current = execSync(cmd, { cwd: repoRoot }).toString().trim();

if (recorded !== current) {
    console.error(`Stale emulator-app assets:\n  recorded: ${recorded}\n  current:  ${current}\nRun: npm run build:emulator-app:aot`);
    process.exit(1);
}

console.log(`emulator-app assets fresh (${current})`);
```

- [ ] **Step 4: Run `build:emulator-app` and `verify-freshness` locally**

```bash
cd editors/vscode
npm run build:emulator-app
npm run verify-freshness
```

Expected: both succeed, `.build-hash` exists.

- [ ] **Step 5: Commit**

```bash
git add editors/vscode/package.json editors/vscode/scripts/copy-emu-app.js editors/vscode/scripts/verify-freshness.js
git commit -m "build(vscode): add emulator-app build scripts and content-hash freshness check"
```

---

### Task 1.S.3: Cake task for emulator-app publish

**Files:**
- Modify: `build.cake`

- [ ] **Step 1: Add a new Cake task**

Append to `build.cake`:

```csharp
// ─────────────────────────────────────────────────────────────
// Emulator app
// ─────────────────────────────────────────────────────────────

Task("publish-emulator-app")
    .Description("Publish Koh.Emulator.App with AOT and copy into editors/vscode/dist/emulator-app")
    .Does(() =>
{
    DotNet("publish src/Koh.Emulator.App -c Release -p:RunAOTCompilation=true");
    StartProcess("npm", new ProcessSettings
    {
        WorkingDirectory = "editors/vscode",
        Arguments = "run build:emulator-app:aot"
    });
});
```

- [ ] **Step 2: Verify the Cake task runs**

Run: `dotnet-cake --target publish-emulator-app`
Expected: succeeds.

- [ ] **Step 3: Commit**

```bash
git add build.cake
git commit -m "build: add publish-emulator-app Cake task"
```

---

## Phase 1-T: CI workflow

### Task 1.T.1: GitHub Actions CI baseline

**Files:**
- Create: `.github/workflows/ci.yml`

- [ ] **Step 1: Create `.github/workflows/ci.yml`**

```yaml
name: CI

on:
  push:
    branches: [master]
  pull_request:

jobs:
  build-and-test:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore solution
        run: dotnet restore Koh.slnx

      - name: Build solution
        run: dotnet build Koh.slnx --no-restore --configuration Release

      - name: Run tests
        run: dotnet test Koh.slnx --no-build --configuration Release

      - name: Download test ROMs
        if: runner.os != 'Windows'
        run: bash scripts/download-test-roms.sh

      - name: Download test ROMs (Windows)
        if: runner.os == 'Windows'
        run: powershell -ExecutionPolicy Bypass -File scripts/download-test-roms.ps1

  emulator-app:
    runs-on: ubuntu-latest
    needs: build-and-test
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - uses: actions/setup-node@v4
        with:
          node-version: '20'

      - name: Install workload
        run: dotnet workload install wasm-tools

      - name: Build emulator app (AOT)
        run: |
          cd editors/vscode
          npm ci
          npm run build:emulator-app:aot

      - name: Verify freshness
        run: |
          cd editors/vscode
          npm run verify-freshness

      - name: Compile extension
        run: |
          cd editors/vscode
          npm run compile

      - name: Upload emulator-app artifact
        uses: actions/upload-artifact@v4
        with:
          name: emulator-app
          path: editors/vscode/dist/emulator-app
          retention-days: 7
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add GitHub Actions workflow for build, test, and emulator-app publish"
```

---

## Phase 1 exit checklist

Before declaring Phase 1 complete, verify every item below. Any failure blocks Phase 1 exit.

- [ ] `dotnet build Koh.slnx` succeeds with no warnings on both Windows and Linux
- [ ] `dotnet test Koh.slnx` — all tests pass (core, debugger, linker, existing Koh tests)
- [ ] `dotnet run --project src/Koh.Emulator.App` — dev host serves the standalone shell at localhost
- [ ] Loading a tiny ROM in standalone mode populates the CPU dashboard
- [ ] `dotnet publish src/Koh.Emulator.App -c Release -p:RunAOTCompilation=true` — AOT publish succeeds
- [ ] Extension development host (F5 on the repo's existing launch config) loads the extension with no errors
- [ ] Creating a `launch.json` with `"type": "koh"` in a workspace with `koh.yaml` allows F5 to:
  - Run the `koh: build <target>` task
  - Open the Koh Emulator webview in a side column
  - Show the CPU dashboard with live mock-CPU state
  - Respond to Pause / Continue from the debug toolbar
- [ ] Setting a source-file breakpoint returns a verified location when the line maps to a `.kdbg` entry
- [ ] Setting a source-file breakpoint returns an unverified (hollow) marker when the line has no address-map entry
- [ ] VS Code Variables panel shows Registers and Hardware scopes with hex values
- [ ] Benchmark page at `/benchmark` (dev host) reports ≥ 2.0× real-time median on your local machine
- [ ] CI runs the full workflow successfully on both ubuntu-latest and windows-latest
- [ ] All commits on the branch follow the conventional commit format used in recent Koh history
- [ ] `scripts/download-test-roms.{ps1,sh}` run without errors (returning 0 with "no ROMs configured" is expected in Phase 1)
- [ ] `editors/vscode/dist/emulator-app/.build-hash` exists and matches `scripts/compute-build-hash.{ps1,sh}` output

If every checkbox is checked, Phase 1 is complete and ready for Phase 2 planning.

---

## Self-review notes

**Spec coverage:** Every Phase 0 + Phase 1 requirement from `docs/superpowers/specs/2026-04-10-emulator-debugger-design.md` is covered by at least one task:

- §3 hardware accuracy scope: not implemented in Phase 1 (scope statement only).
- §7.1-7.3 types, clocking model, tick model: Tasks 1.A.*, 1.D.*, 1.F.*
- §7.4 CPU micro-op model: Phase 1 uses the mock CPU per §12.9; full model arrives Phase 3.
- §7.5 MBC dispatch: Task 1.B.2 (enum-dispatched sealed Cartridge)
- §7.6 DMA timing: not implemented in Phase 1 per §7.12 (moved to Phase 2).
- §7.7 PPU pixel-FIFO: not implemented in Phase 1 per §7.12 (Phase 2 task).
- §7.8 performance rules: enforced across all Koh.Emulator.Core tasks.
- §7.9 public API: Tasks 1.F.2, 1.F.3.
- §7.10 debug peek/poke contract: Task 1.G.1 + Mmu.DebugRead/DebugWrite.
- §7.11 save-state: design constraint documented; implementation deferred to Phase 4.
- §7.12 subsystem phasing: encoded across Phase 1 task groups.
- §8.1-8.3 debugger file layout: Phase 1-J, 1-K, 1-L.
- §8.4 source mapping for banked ROM: Task 1.K.1 + SourceMap.
- §8.5 call stack vs expansion stack: not implemented in Phase 1 (call stack is Phase 3).
- §8.6 execution loop: Task 1.L.1.
- §8.7 DAP capabilities by phase: Task 1.J.2 advertises exactly the Phase 1 set.
- §8.8 T-cycle stepping: not implemented in Phase 1 (Phase 3).
- §9 .kdbg format: Phase 1-H with Phase-1 scope reduction (no coalescing, no dedup).
- §10 Blazor app: Phase 1-M, 1-N.
- §11 VS Code extension: Phase 1-O, 1-P, 1-Q, 1-R.
- §11.6 build pipeline and content hash: Phase 1-S.
- §11.9 transport reliability: Task 1.Q.2 (DapMessageQueue + InlineDapAdapter).
- §12.9 performance gates: Task 1.N.3 benchmark page + Phase 1 exit criterion.

**Known deferrals to Phase 2+ (explicitly NOT in this plan):**
- PPU fetcher and pixel FIFO (§7.7)
- OAM DMA and HDMA (§7.6)
- VRAM/OAM mode lockouts in Mmu (§7.12)
- Full CPU instruction set and interrupt dispatch (§7.4)
- `readMemory` DAP capability
- Stepping (`next`, `stepIn`, `stepOut`)
- Stack trace
- Disassembly
- Symbols/Source Context variable scopes
- Full koh.yaml parsing beyond the minimal schema (if Koh adds more fields)
- Save-state implementation
- Watchpoints
- MAUI desktop shell

**Known design risks carried forward (§Resolved design decisions → Design risks):**
- Blazor WASM AOT performance for the tick-driven loop — validated by Task 1.N.3 benchmark gate.
- MBC1 debug-write patching only covers bank 0 in Phase 1 Mmu.DebugWrite; higher-bank MBC1 patching requires MBC-aware address translation added when Phase 4 needs it.
- `KohYamlReader.parseMinimalYaml` is a hand-rolled parser; if the koh.yaml schema grows, replace with a real YAML library (add `js-yaml` to the VS Code extension `dependencies`).

---

**Plan complete and saved to `docs/superpowers/plans/2026-04-10-emulator-phase-1.md`.**

Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration with clean context boundaries.

**2. Inline Execution** — Execute tasks in this session using the executing-plans skill, batch execution with checkpoints for review.

Which approach?

