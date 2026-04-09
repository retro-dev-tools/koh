# LSP Native AOT Feasibility Assessment

**Date:** 2026-04-09
**Runtime identifier:** win-x64
**Target framework:** net10.0
**Build outcome:** FAILED (5 errors, 0 warnings)

## Build Configuration

`<PublishAot>true</PublishAot>` was added to `Koh.Lsp.csproj` and a release publish was attempted:

```
dotnet publish src/Koh.Lsp -c Release -r win-x64 -o publish/win-x64/lsp
```

## Errors Encountered

### 1. YamlDotNet — `DeserializerBuilder` requires dynamic code (IL3050)

**Location:** `Config/KohProjectFileLoader.cs:48`
**Root cause:** `DeserializerBuilder()` uses reflection-based deserialization internally. The constructor is annotated with `[RequiresDynamicCode]`.
**Fix available:** Yes. YamlDotNet 16.x ships a `StaticDeserializerBuilder` designed for AOT. Requires generating a static YAML context class (source generator or manual `StaticContext` subclass). Straightforward migration.
**Estimated effort:** Low (1-2 hours). Replace `DeserializerBuilder` with `StaticDeserializerBuilder`, create a static context with the single type `KohProjectFileYaml`.

### 2. StreamJsonRpc — `HeaderDelimitedMessageHandler` constructor (IL2026, IL3050)

**Location:** `Program.cs:6`
**Root cause:** The default `HeaderDelimitedMessageHandler(Stream, Stream)` constructor creates a `JsonMessageFormatter` internally, which relies on `Newtonsoft.Json` with reflection-based serialization. Both `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]` are present.
**Fix available:** Partial. StreamJsonRpc 2.x supports pluggable formatters. A `SystemTextJsonFormatter` exists but itself requires `System.Text.Json` source generators for full AOT compatibility. The LSP protocol types from `Microsoft.VisualStudio.LanguageServer.Protocol` use Newtonsoft.Json attributes, so switching formatters would require replacing or adapting the protocol type library as well.
**Estimated effort:** High (days to weeks). Would require either:
  - (a) Switching to `SystemTextJsonFormatter` + writing `JsonSerializerContext` source generators for all LSP protocol types, or
  - (b) Replacing `Microsoft.VisualStudio.LanguageServer.Protocol` with a different LSP types library that supports System.Text.Json, or
  - (c) Waiting for upstream AOT support in StreamJsonRpc / MS LSP Protocol packages.

### 3. StreamJsonRpc — `JsonRpc.AddLocalRpcTarget(object)` (IL2026, IL3050)

**Location:** `Program.cs:10`
**Root cause:** `AddLocalRpcTarget` discovers RPC methods on the target object via reflection at runtime. It uses `Type.GetMethods()` and constructs generic delegates dynamically. Both trimming and AOT annotations flag this.
**Fix available:** Not directly. This is fundamental to how StreamJsonRpc dispatches calls. The typed overload `AddLocalRpcTarget<T>()` exists but still uses reflection internally. A fully AOT-safe RPC dispatch would require a source-generated RPC stub, which StreamJsonRpc does not yet provide.
**Estimated effort:** Very high if done manually. Would require writing a custom `IJsonRpcMessageHandler` or switching to a different LSP framework entirely.

## Smoke Test Results

Not applicable — the binary did not compile.

## Analysis

The blockers fall into two categories:

1. **YamlDotNet (solvable):** The static deserialization API is available and well-documented. This is a routine migration.

2. **StreamJsonRpc + MS LSP Protocol types (blocking):** The entire JSON-RPC transport layer depends on reflection for both message serialization (Newtonsoft.Json) and RPC method dispatch. These are architectural dependencies, not isolated call sites. StreamJsonRpc v2.24 does not ship source generators for AOT-safe dispatch, and the MS LSP Protocol types are bound to Newtonsoft.Json.

The StreamJsonRpc/LSP Protocol stack is the primary blocker. Until Microsoft ships AOT-compatible versions of these packages (or a community alternative matures), enabling Native AOT for `koh-lsp` would require replacing the transport layer entirely.

## Recommendation

**Defer Native AOT for koh-lsp.** Accept managed-only distribution for the LSP server.

**Rationale:**
- The two blocking dependencies (StreamJsonRpc, Microsoft.VisualStudio.LanguageServer.Protocol) are owned by Microsoft and are the standard .NET LSP stack. Replacing them would be a large effort with ongoing maintenance cost.
- LSP servers are long-running processes where startup time matters less than for CLI tools. The primary AOT benefit (fast cold start) is less impactful here.
- The VS Code extension already bundles the managed runtime, so there is no distribution size concern specific to the LSP server.
- Microsoft is actively working on AOT support across the .NET ecosystem. Re-evaluate when StreamJsonRpc ships AOT-compatible dispatch (track [StreamJsonRpc GitHub](https://github.com/microsoft/vs-streamjsonrpc)).

**Re-evaluation trigger:** StreamJsonRpc releases a version with `[UnconditionalSuppressMessage]` removed from core APIs, or ships source-generated RPC stubs.

**Contrast with CLI tools:** `koh` (the CLI compiler) was successfully published as Native AOT in Task 4. The LSP server has fundamentally different dependency characteristics due to the RPC transport layer.
