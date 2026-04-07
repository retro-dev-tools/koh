# Plan: LSP Entrypoint Discovery, Project Contexts, and `koh.yaml`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Do not skip steps. Do not replace explicit configuration with heuristics where this plan requires strict behavior.

## Goal

Implement a Roslyn-like LSP workspace model for RGBDS projects with an explicit `koh.yaml` project file.

The final system must:

* use `koh.yaml` as the authoritative source of project entrypoints when present
* fall back to heuristic entrypoint discovery only when `koh.yaml` is absent
* keep one compilation per entrypoint
* analyze files inside the correct project context
* use unsaved in-memory document contents for discovery and compilation
* avoid cross-project symbol and diagnostic leakage
* allow the VS Code extension to generate `koh.yaml`

## Non-goals

* [ ] Do **not** introduce a build system
* [ ] Do **not** parse arbitrary `Makefile` logic
* [ ] Do **not** create one giant workspace-wide compilation
* [ ] Do **not** silently ignore invalid `koh.yaml`
* [ ] Do **not** fall back to heuristics when a folder contains invalid `koh.yaml`
* [ ] Do **not** push workspace/discovery logic into `Koh.Core`
* [ ] Do **not** attach unattributed diagnostics to every open file
* [ ] Do **not** assume a file has exactly one global semantic meaning outside a project context

---

## Required architecture

* [ ] `koh.yaml` is authoritative for a workspace folder if present and valid
* [ ] Heuristics are used only when `koh.yaml` is absent
* [ ] Invalid `koh.yaml` puts the folder into configuration-error mode
* [ ] One `Compilation` exists per configured or discovered entrypoint
* [ ] A file can belong to multiple project contexts
* [ ] The workspace chooses one deterministic primary project context per file
* [ ] Discovery uses overlay text from open documents, not just disk
* [ ] Semantic APIs must be project-context-aware

---

## Files to create

* [ ] `src/Koh.Lsp/Config/KohProjectFileLoader.cs`
* [ ] `src/Koh.Lsp/Config/KohProjectFileModels.cs`
* [ ] `src/Koh.Lsp/Discovery/IncludeDiscoveryService.cs`
* [ ] `src/Koh.Lsp/Discovery/WorkspaceGraph.cs`
* [ ] `src/Koh.Lsp/Discovery/EntrypointDiscoveryService.cs`
* [ ] `src/Koh.Lsp/Projects/ProjectContext.cs`
* [ ] `src/Koh.Lsp/Projects/ProjectContextManager.cs`
* [ ] `src/Koh.Lsp/Source/WorkspaceOverlayResolver.cs`
* [ ] `tests/Koh.Lsp.Tests/Config/KohProjectFileLoaderTests.cs`
* [ ] `tests/Koh.Lsp.Tests/Discovery/IncludeDiscoveryServiceTests.cs`
* [ ] `tests/Koh.Lsp.Tests/Discovery/EntrypointDiscoveryServiceTests.cs`
* [ ] `tests/Koh.Lsp.Tests/Projects/ProjectContextManagerTests.cs`

## Files to modify

* [ ] `src/Koh.Lsp/Workspace.cs`
* [ ] `src/Koh.Lsp/KohLanguageServer.cs`
* [ ] `editors/vscode/src/extension.ts`

---

## Phase 1 — Define configuration model

### Task 1.1 — Add `koh.yaml` models

* [ ] Create `KohProjectFileModels.cs`
* [ ] Define strict models for:

  * [ ] top-level config
  * [ ] per-project definition
  * [ ] config load result
  * [ ] config validation errors
* [ ] Include a folder mode enum with:

  * [ ] `Configured`
  * [ ] `Heuristic`
  * [ ] `InvalidConfiguration`

### Task 1.2 — Define v1 schema in code

* [ ] Support this minimal schema:

```yaml
version: 1
projects:
  - name: game
    entrypoint: src/game.asm
```

* [ ] Require `version`
* [ ] Require `projects`
* [ ] Require each project to have `name`
* [ ] Require each project to have `entrypoint`
* [ ] Reject empty `projects`
* [ ] Reject duplicate project names within a folder
* [ ] Reject duplicate normalized entrypoints within a folder
* [ ] Treat entrypoints as workspace-folder-relative by default

### Task 1.3 — Load and validate `koh.yaml`

* [ ] Create `KohProjectFileLoader.cs`
* [ ] Load only `koh.yaml` from the workspace folder root
* [ ] Parse YAML strictly
* [ ] Normalize entrypoint paths to absolute normalized paths
* [ ] Return one of:

  * [ ] valid configured project list
  * [ ] config file missing
  * [ ] invalid config with structured errors

### Task 1.4 — Enforce invalid-config behavior

* [ ] When `koh.yaml` exists but is invalid, mark the folder as `InvalidConfiguration`
* [ ] Do **not** silently fall back to heuristics
* [ ] Surface a clear configuration diagnostic/loggable error

### Task 1.5 — Add config loader tests

* [ ] Test valid single-project config
* [ ] Test valid multi-project config
* [ ] Test missing config
* [ ] Test invalid YAML
* [ ] Test missing required fields
* [ ] Test duplicate project names
* [ ] Test duplicate normalized entrypoints
* [ ] Test relative path normalization
* [ ] Test invalid config blocks heuristic fallback semantics

---

## Phase 2 — Add overlay-aware source access

### Task 2.1 — Create overlay resolver

* [ ] Create `WorkspaceOverlayResolver.cs`
* [ ] Wrap disk-based resolution rather than replacing it
* [ ] Use overlay text for open documents
* [ ] Use disk contents for unopened files
* [ ] Keep binary reads on disk for `INCBIN`
* [ ] Normalize paths consistently

### Task 2.2 — Verify resolver behavior

* [ ] Ensure `FileExists` respects overlays
* [ ] Ensure text reads prefer overlay text
* [ ] Ensure path resolution still delegates to the real file resolver
* [ ] Ensure missing overlays fall back to disk

### Task 2.3 — Add focused tests or coverage through manager tests

* [ ] Verify unsaved text is visible through the resolver
* [ ] Verify disk fallback works after document close
* [ ] Verify `INCBIN` byte reads remain disk-backed

---

## Phase 3 — Build include discovery

### Task 3.1 — Implement `IncludeDiscoveryService`

* [ ] Create `IncludeDiscoveryService.cs`
* [ ] Implement a lightweight scanner or lexer-style helper
* [ ] Do **not** use a single raw regex as the only mechanism
* [ ] Ignore comments
* [ ] Handle case-insensitive `INCLUDE`
* [ ] Tolerate malformed/incomplete files
* [ ] Resolve include targets relative to the containing file
* [ ] Return `FileDiscoveryInfo`

### Task 3.2 — Support overlay-aware discovery

* [ ] Allow discovery from explicit in-memory text
* [ ] Allow discovery from disk for unopened files
* [ ] Keep path normalization consistent with compiler resolution

### Task 3.3 — Add include discovery tests

* [ ] Test simple include extraction
* [ ] Test case-insensitive keyword handling
* [ ] Test commented-out includes are ignored
* [ ] Test malformed files do not crash discovery
* [ ] Test relative path resolution
* [ ] Test overlay text overrides disk text

---

## Phase 4 — Build workspace graph

### Task 4.1 — Implement `WorkspaceGraph`

* [ ] Create `WorkspaceGraph.cs`
* [ ] Store per-file include edges
* [ ] Store reverse edges
* [ ] Store file discovery versions/state
* [ ] Support incremental updates for one file
* [ ] Support file removal
* [ ] Support reachability queries

### Task 4.2 — Required APIs

* [ ] Implement `UpsertFile(FileDiscoveryInfo info)`
* [ ] Implement `RemoveFile(string path)`
* [ ] Implement `GetIncludes(string path)`
* [ ] Implement `GetIncluders(string path)`
* [ ] Implement `GetReachableFiles(string entrypointPath)`
* [ ] Implement `GetReachableEntrypoints(string filePath, IEnumerable<string> entrypoints)`

### Task 4.3 — Handle cycles safely

* [ ] Ensure graph traversal cannot recurse forever on cycles
* [ ] Ensure cycle handling is deterministic

### Task 4.4 — Add graph coverage through discovery/manager tests

* [ ] Verify updating one file rewrites its edges correctly
* [ ] Verify deleting a file removes its edges
* [ ] Verify cycles do not break reachability

---

## Phase 5 — Implement heuristic entrypoint discovery

### Task 5.1 — Implement `EntrypointDiscoveryService`

* [ ] Create `EntrypointDiscoveryService.cs`
* [ ] Scan all relevant files in an unconfigured workspace folder
* [ ] Use the graph to derive candidate entrypoints
* [ ] Rank candidates deterministically
* [ ] Compute file ownership from candidates

### Task 5.2 — Candidate ranking rules

* [ ] Highest confidence: `.asm` file with outgoing includes and no incoming includes
* [ ] Next: `.asm` file directly opened and not owned by a stronger candidate
* [ ] Next: standalone `.asm` file
* [ ] Lowest-confidence fallback: directly opened `.inc` file for standalone analysis
* [ ] Do **not** hard-ban `.inc` from standalone analysis

### Task 5.3 — Ownership rules

* [ ] For each file, collect all reachable candidate entrypoints
* [ ] Pick a deterministic primary owner using:

  * [ ] highest score
  * [ ] shortest include distance
  * [ ] stable path tie-breaker

### Task 5.4 — Add entrypoint discovery tests

* [ ] Test single-entrypoint workspace
* [ ] Test multi-entrypoint workspace
* [ ] Test standalone `.asm` fallback
* [ ] Test direct-open `.inc` standalone fallback
* [ ] Test shared include owned by multiple entrypoints
* [ ] Test deterministic primary-owner selection
* [ ] Test cycle handling

---

## Phase 6 — Introduce project contexts

### Task 6.1 — Define `ProjectContext`

* [ ] Create `ProjectContext.cs`
* [ ] Include at minimum:

  * [ ] stable ID
  * [ ] project name
  * [ ] entrypoint path
  * [ ] reachable file set
  * [ ] compilation instance
  * [ ] graph version

### Task 6.2 — Implement `ProjectContextManager`

* [ ] Create `ProjectContextManager.cs`
* [ ] Build one compilation per entrypoint
* [ ] Maintain file-to-owners mapping
* [ ] Maintain deterministic primary owner
* [ ] Rebuild only affected project contexts
* [ ] Keep configured and heuristic modes separate per folder

### Task 6.3 — Required APIs

* [ ] Implement `InitializeWorkspaceFolder(...)`
* [ ] Implement `UpdateDocumentText(...)`
* [ ] Implement `RemoveDocument(...)`
* [ ] Implement `ReloadConfiguration(...)`
* [ ] Implement `GetProjectContextsFor(string filePath)`
* [ ] Implement `GetPrimaryProjectContextFor(string filePath)`
* [ ] Implement `RebuildAffectedProjects(...)`

### Task 6.4 — Compilation model

* [ ] For each entrypoint, create a `Compilation` with `WorkspaceOverlayResolver`
* [ ] Use the entrypoint syntax tree as the root
* [ ] Allow transitive includes to load through the resolver
* [ ] Do **not** merge unrelated entrypoints into one compilation

### Task 6.5 — Configured-mode rules

* [ ] If folder mode is `Configured`, use only `koh.yaml` entrypoints
* [ ] Do **not** mix in heuristic candidates for normal semantics
* [ ] If a file is outside all configured projects, leave it outside configured ownership

### Task 6.6 — Heuristic-mode rules

* [ ] If folder mode is `Heuristic`, use discovered entrypoints and ownership
* [ ] Preserve deterministic primary-owner selection

### Task 6.7 — Invalid-config rules

* [ ] If folder mode is `InvalidConfiguration`, do not build heuristic project contexts for the folder
* [ ] Surface config failure through diagnostics/logging paths

### Task 6.8 — Add project context manager tests

* [ ] Test one compilation per entrypoint
* [ ] Test configured projects override heuristics
* [ ] Test shared include edits rebuild only affected projects
* [ ] Test unrelated projects remain isolated
* [ ] Test file outside configured projects gets standalone analysis path, not heuristic ownership
* [ ] Test invalid configuration blocks heuristic fallback

---

## Phase 7 — Refactor workspace to be project-context-aware

### Task 7.1 — Update `Workspace.cs` responsibilities

* [ ] Track open documents and overlay text
* [ ] Track per-folder mode
* [ ] Delegate config loading to `KohProjectFileLoader`
* [ ] Delegate discovery to `EntrypointDiscoveryService`
* [ ] Delegate compilation ownership to `ProjectContextManager`

### Task 7.2 — File analysis behavior

* [ ] If file belongs to one project context, use it
* [ ] If file belongs to multiple project contexts, use deterministic primary owner by default
* [ ] If file is in configured folder but outside all configured projects, analyze standalone
* [ ] If file is in heuristic folder and has no owner, analyze standalone
* [ ] Do **not** invent a fake global workspace semantic model

### Task 7.3 — Diagnostics behavior

* [ ] Syntax diagnostics come from the file’s own parse
* [ ] Semantic diagnostics come from the selected project context
* [ ] Diagnostics with `FilePath == null` must not be attached to every open file
* [ ] Attach unattributed diagnostics to the relevant entrypoint document only
* [ ] Do not leak diagnostics across unrelated projects

### Task 7.4 — Project-context-ready APIs

* [ ] Ensure newly introduced workspace APIs accept project context where semantic meaning depends on it
* [ ] Do not add new file-only semantic APIs that assume one global meaning

---

## Phase 8 — Wire LSP lifecycle correctly

### Task 8.1 — Initialize folder state

* [ ] In `KohLanguageServer.cs`, capture workspace folders during initialization
* [ ] Initialize per-folder state rather than relying on one global CWD-based project identity

### Task 8.2 — Initial build

* [ ] On `Initialized`, for each workspace folder:

  * [ ] load `koh.yaml`
  * [ ] if valid, initialize configured mode
  * [ ] if missing, initialize heuristic mode
  * [ ] if invalid, initialize invalid-config mode
* [ ] Build initial project contexts

### Task 8.3 — `DidOpen`

* [ ] Add overlay text
* [ ] Rescan discovery from in-memory text
* [ ] Recompute affected ownership
* [ ] Rebuild affected projects only
* [ ] Publish diagnostics

### Task 8.4 — `DidChange`

* [ ] Update overlay text immediately
* [ ] Rescan discovery from in-memory text
* [ ] Recompute affected ownership
* [ ] Rebuild affected projects only
* [ ] Publish diagnostics
* [ ] Do **not** defer discovery until save

### Task 8.5 — `DidSave`

* [ ] Refresh disk-backed state if needed
* [ ] If saved file is `koh.yaml`, reload configuration for that folder
* [ ] Rebuild affected project contexts
* [ ] Republish diagnostics

### Task 8.6 — `DidClose`

* [ ] Remove overlay text
* [ ] Revert that document to disk-backed discovery
* [ ] Recompute affected ownership
* [ ] Rebuild affected projects only
* [ ] Republish diagnostics

### Task 8.7 — LSP integration tests

* [ ] Test opening only an included file resolves symbols through the owning entrypoint
* [ ] Test unsaved include edits change ownership immediately
* [ ] Test valid `koh.yaml` disables heuristic project selection in that folder
* [ ] Test invalid `koh.yaml` reports config error and does not silently fall back
* [ ] Test unattributed diagnostics are attached only to entrypoint
* [ ] Test `DidClose` restores disk-backed ownership
* [ ] Test multiple workspace folders stay isolated

---

## Phase 9 — Upgrade VS Code logging

### Task 9.1 — Switch to `LogOutputChannel`

* [ ] Modify `editors/vscode/src/extension.ts`
* [ ] Replace plain `OutputChannel` with `LogOutputChannel`
* [ ] Use `createOutputChannel('Koh', { log: true })`

### Task 9.2 — Replace string-prefixed logging

* [ ] Replace manual `[info]`, `[warn]`, `[error]` prefixes
* [ ] Use `log.info(...)`
* [ ] Use `log.warn(...)`
* [ ] Use `log.error(...)`

### Task 9.3 — Ensure useful logging points exist

* [ ] Log config load result per folder
* [ ] Log transition between configured/heuristic/invalid-config modes
* [ ] Log project generation command activity
* [ ] Log ownership ambiguity only when helpful, not noisily

---

## Phase 10 — Add `koh.yaml` generation in the VS Code extension

### Task 10.1 — Add command

* [ ] Add command `Koh: Generate koh.yaml`
* [ ] Expose it through the command palette
* [ ] Make it available only when a workspace folder is open

### Task 10.2 — Generate initial file contents

* [ ] If heuristics find entrypoints, prefill `projects` from discovered candidates
* [ ] Order generated projects by heuristic confidence
* [ ] If heuristics find nothing useful, generate a minimal template
* [ ] Ensure generated YAML matches the v1 schema

### Task 10.3 — Prompting behavior

* [ ] When no `koh.yaml` exists and the folder is running in heuristic mode, optionally prompt the user to generate it
* [ ] Make the prompt non-blocking
* [ ] Prompt at most once per workspace folder until dismissed or file generated
* [ ] Do not prompt if valid `koh.yaml` already exists
* [ ] Do not prompt if folder is in invalid-config mode; surface the config problem instead

### Task 10.4 — Extension tests

* [ ] Test the generate command writes valid `koh.yaml`
* [ ] Test heuristic candidates are used to prefill the generated file
* [ ] Test prompt appears only in appropriate cases
* [ ] Test prompt does not repeat excessively

---

## Phase 11 — Validate semantics and isolation

### Task 11.1 — Cross-project isolation checks

* [ ] Verify unrelated entrypoints do not share symbol space
* [ ] Verify diagnostics do not leak across projects
* [ ] Verify shared includes can belong to multiple projects without collapsing them into one semantic world

### Task 11.2 — Standalone behavior checks

* [ ] Verify orphan `.asm` files still get standalone analysis
* [ ] Verify directly opened `.inc` files can still get last-resort standalone analysis in heuristic mode
* [ ] Verify files outside configured projects are not auto-assigned to heuristic projects

### Task 11.3 — Project-context API audit

* [ ] Review any new workspace/semantic APIs added during implementation
* [ ] Remove or refactor any API that assumes a single global semantic model for a file

---

## Phase 12 — Final verification

### Task 12.1 — Automated tests

* [ ] Run `dotnet test --project tests/Koh.Lsp.Tests`
* [ ] Run `dotnet test --project tests/Koh.Core.Tests`

### Task 12.2 — Build verification

* [ ] Publish the server
* [ ] Rebuild the VS Code extension
* [ ] Confirm extension activation and command registration work

### Task 12.3 — Manual verification scenarios

* [ ] Workspace with valid `koh.yaml` uses configured entrypoints only
* [ ] Workspace without `koh.yaml` falls back to heuristics
* [ ] Workspace with invalid `koh.yaml` shows config error and does not silently fall back
* [ ] Opening a non-root included file resolves symbols through the owning entrypoint
* [ ] Unsaved include edits affect diagnostics before save
* [ ] Shared include changes rebuild only affected projects
* [ ] Unrelated roots do not leak diagnostics
* [ ] Generated `koh.yaml` is valid and immediately usable
* [ ] Output channel uses leveled logs

---

## Hard requirements for the implementing agent

* [ ] Do not simplify this into one global workspace compilation
* [ ] Do not use `DidSave` as the primary invalidation point
* [ ] Do not silently ignore invalid `koh.yaml`
* [ ] Do not attach `FilePath == null` diagnostics to every file
* [ ] Do not mix configured and heuristic roots in a configured folder
* [ ] Do not add new semantic APIs that lack project context where project context matters
* [ ] Do not replace the scanner with a naive one-regex implementation
* [ ] Do not treat `.inc` as impossible to analyze standalone
* [ ] Do not move discovery/configuration concerns into `Koh.Core`

## Completion criteria

This plan is complete only when all of the following are true:

* [ ] `koh.yaml` is authoritative when valid
* [ ] missing `koh.yaml` falls back to heuristics
* [ ] invalid `koh.yaml` blocks heuristic fallback for that folder
* [ ] one compilation exists per entrypoint
* [ ] overlay text affects discovery and compilation before save
* [ ] files are analyzed in the correct project context
* [ ] unrelated projects are isolated
* [ ] the extension can generate `koh.yaml`
* [ ] tests cover configured mode, heuristic mode, invalid-config mode, and overlay-driven updates
