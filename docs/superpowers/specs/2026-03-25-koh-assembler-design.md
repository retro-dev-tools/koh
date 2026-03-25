# Koh — A Modern Game Boy Assembler Platform

**Date**: 2026-03-25
**Status**: Approved

## Vision

Koh is a "Roslyn for GB ASM" — a modern C# assembler and linker platform for the Game Boy, designed as a library-first compiler that powers CLI tools, an LSP server, and future analysis/refactoring tools from a single source of truth.

Named after Koh from Azure Dreams.

## Goals

- **100% RGBDS `.asm` syntax compatibility** — drop-in replacement for `rgbasm` input
- **Modern compiler architecture** — red-green syntax trees, immutable semantic model, incremental reparsing
- **Better-than-RGBDS diagnostics** — every error carries full source spans, suggestions, and explanations
- **Constraint-based linker** — provably optimal section placement with clear failure explanations
- **Library-first** — CLI, LSP, and third-party tools all consume the same `Koh.Core` API
- **General-purpose GB dev tool** — for the retro dev community, not just one project

## Non-Goals

- Graphics conversion (rgbgfx equivalent) — separate concern, out of scope
- Compatibility with RGBDS object file format as the primary format (available as export mode)
- SDCC object format support

---

## Solution Structure

```
Koh.sln
├── src/
│   ├── Koh.Core/            # Syntax trees, parser, semantic model, symbols
│   ├── Koh.Emit/            # Object file generation (Koh format + RGBDS export)
│   ├── Koh.Linker.Core/     # Section placement, symbol resolution, ROM output
│   ├── Koh.Asm/             # CLI assembler (thin consumer of Core + Emit)
│   ├── Koh.Link/            # CLI linker (thin consumer of Linker.Core)
│   └── Koh.Lsp/             # LSP server (thin consumer of Core)
├── tests/
│   ├── Koh.Core.Tests/
│   ├── Koh.Emit.Tests/
│   ├── Koh.Linker.Tests/
│   └── Koh.Compat.Tests/   # Byte-for-byte comparison against RGBDS output
└── docs/
```

**Koh.Core** is the heart — everything a consumer needs to parse, analyze, and query GB assembly source. No I/O, no console output, pure computation returning structured results.

**Koh.Emit** handles object file serialization. The RGBDS export mode lives here — it takes Koh's semantic model and downgrades it to RGB9 format (including converting expression trees to RPN).

**Koh.Linker.Core** is a library so the LSP can do link-level analysis (cross-file symbol resolution, bank usage visualization) without shelling out to the CLI.

**Koh.Asm** and **Koh.Link** are thin CLI frontends.

**Koh.Lsp** is the LSP server, replacing the current TypeScript/Tree-sitter rgbds-lsp.

---

## Red-Green Syntax Tree

### Green Nodes (Internal)

- **Immutable**, no parent pointers, no absolute positions
- Store: `SyntaxKind` enum, width (character count), child green nodes, leading/trailing trivia
- **Trivia**: whitespace, comments, line continuations — attached to tokens, never lost
- **Cacheable**: identical subtrees share the same green node instance (structural sharing)
- Created by the parser, never modified after construction

### Red Nodes (Public API)

- Thin wrappers around green nodes
- Add: `.Parent`, `.Position` (absolute offset), `.Span`
- **Created on demand** — walking `.Children` wraps each green child lazily
- This is what consumers (LSP, analyzers, CLI) interact with

### SyntaxKind Enum

One enum covering all node types:

- **Tokens**: `IdentifierToken`, `NumberLiteral`, `StringLiteral`, `CommaToken`, `NewlineToken`, `EndOfFileToken`, plus one per keyword (`LdKeyword`, `AddKeyword`, `SectionKeyword`, `DbKeyword`...)
- **Trivia**: `WhitespaceTrivia`, `LineCommentTrivia`, `BlockCommentTrivia`
- **Nodes**: `CompilationUnit`, `InstructionStatement`, `DirectiveStatement`, `LabelDeclaration`, `MacroDefinition`, `MacroInvocation`, `SectionDirective`, `BinaryExpression`, `UnaryExpression`, `ParenthesizedExpression`...

### SyntaxTree

- `SyntaxTree.Parse(SourceText)` → returns a `SyntaxTree`
- Holds the green root, the `SourceText`, and diagnostics produced during parsing
- `SourceText` is an abstraction over source content (file, string, or modified buffer)
- Diagnostics are collected during parsing but never thrown — the tree is always produced, even with errors

### Error Recovery

- The parser **always** produces a complete tree, even for broken input
- Missing tokens are inserted as zero-width `MissingToken` nodes
- Unexpected tokens are wrapped in `SkippedTokensTrivia`
- Critical for IDE scenarios — every keystroke produces a valid tree

---

## Parser Pipeline

### Lexer

- Hand-written, produces a flat stream of `SyntaxToken` green nodes
- Each token carries leading and trailing trivia lists
- Mode-based:
  - **Normal** — standard tokenization
  - **Raw** — for macro invocation arguments (rest of line as string tokens)
  - **String** — inside string literals, handles escape sequences and `{interpolation}`
- Returns tokens lazily (iterator/enumerator) — the parser pulls as needed

### Parser

- Hand-written recursive descent consuming the token stream
- One method per grammar rule: `ParseCompilationUnit()`, `ParseStatement()`, `ParseInstruction()`, `ParseExpression()`, etc.
- Expression parsing uses **Pratt parsing** (precedence climbing) for binary operators
- Produces green nodes bottom-up, returned as `SyntaxTree`

### SourceText

- Immutable representation of source content
- Tracks line starts for efficient line/column lookup from absolute offset
- Supports `WithChanges(TextChange[])` for incremental scenarios

---

## Semantic Model

### Compilation

- `Compilation.Create(SyntaxTree[])` — takes one or more parsed files
- Holds the full program state: all syntax trees, symbol table, diagnostics
- Immutable — `.AddSyntaxTrees()` or `.ReplaceSyntaxTree()` returns a new `Compilation`
- Entry point for the LSP: parse on keystroke, create new compilation, query it

### Symbol Table

- **Symbols** are semantic objects, separate from syntax: `LabelSymbol`, `ConstantSymbol`, `MacroSymbol`, `StringSymbol`, `SectionSymbol`
- Resolved during **binding**
- Scoping: global symbols, local labels (`.dot` prefix scoped to enclosing global label), macro-local via `\@`
- Forward references tracked and resolved in a second pass
- Exports/imports recorded per symbol for linker consumption

### Binder

- Walks each `SyntaxTree`, produces a **bound tree** (lowered representation)
- Resolves symbol references to `Symbol` objects
- Evaluates constant expressions where possible (folds `EQU`, `EQUS`, `DEF`)
- Link-time dependent expressions remain as **deferred expressions** — tree-structured, preserving source spans for diagnostics
- Produces semantic diagnostics: undefined symbols, duplicate definitions, type mismatches, section constraint violations

### SemanticModel

- Per-file view into the compilation: `compilation.GetSemanticModel(syntaxTree)`
- API for consumers: `GetSymbol(node)`, `GetDiagnostics()`, `GetDeclaredSymbol(node)`, `LookupSymbols(position)`
- Primary API the LSP uses for hover, go-to-definition, find references

---

## Instruction Encoding

### SM83 Instruction Database

- Static table mapping `(mnemonic, operand patterns)` → `(opcode bytes, size, cycles)`
- Modeled as data, not code — a record per encoding variant
- The binder pattern-matches parsed instruction nodes against this table
- Unmatched patterns produce diagnostics with suggestions

### Encoding

- Instructions with known operands are encoded immediately
- Instructions with deferred operands emit a **patch** — instruction bytes with placeholder + deferred expression for link-time resolution
- Relative jumps (`JR`) encode as `target - current_pc`, validated for signed 8-bit range

### Directives

All 51 RGBDS directives supported:

- `DB`, `DW`, `DL` — literal bytes/words/longs, support deferred expressions
- `DS` — reserve space, optional fill byte
- `SECTION` — opens section context with type, bank, address, alignment constraints
- `INCLUDE` / `INCBIN` — resolved via `SourceFileResolver` interface (abstractable for LSP virtual file systems)
- Macro expansion at syntax level — binder expands invocations by substituting arguments, re-parsing, binding the expanded tree

---

## Object Format & Emitter

### Koh Object Format (.kobj)

- Designed for richness:
  - **Header**: magic, version, feature flags
  - **Source map**: file paths + line/column spans for every emitted byte
  - **Symbols**: name, kind, visibility, defining location, value or deferred expression
  - **Sections**: name, type, constraints, data, patches
  - **Expressions**: serialized as expression trees (not RPN) — preserving structure for diagnostics, each node carries source span
  - **Diagnostics**: assembler warnings/infos carried through to linker for unified reporting
- Binary format with well-documented spec for third-party tooling

### RGBDS Export Mode

- `--format rgbds` flag emits RGB9 rev 13 `.o` files
- Flattens expression trees to RPN (lossy — source spans dropped)
- Maps Koh symbol/section types to RGBDS equivalents
- Validates no Koh-only features are used
- Enables gradual migration: mix Koh and RGBASM objects, link with either tool

---

## Linker

### Architecture

- `LinkerCompilation.Create(ObjectFile[])` — loads objects, returns immutable linker model
- Library-first: CLI and LSP both consume `Koh.Linker.Core`

### Symbol Resolution

- Merge symbol tables from all object files
- Match imports to exports by name
- Detect conflicts: duplicate exports, unresolved imports
- Rich diagnostics with full source location chains

### Section Placement — Constraint Solver

Instead of RGBDS's greedy first-fit-decreasing:

- Model placement as a **constraint satisfaction problem**:
  - Fixed bank / fixed address constraints
  - Alignment with offset
  - Fragment co-location (same bank)
  - Per-bank capacity limits (ROM0: 16K, ROMX: 16K, WRAM0: 4K, etc.)
- Custom backtracking solver, with potential Z3 integration for provably optimal placement
- **Key advantage**: on failure, the solver explains *why* — "sections A, B, C together exceed bank capacity" instead of opaque "section won't fit"

### Expression Evaluation

- Evaluate deferred expressions with all addresses known
- Walk expression trees — on failure, error points to exact subexpression and source location
- Evaluate `ASSERT` directives with full context

### ROM Output

- Assemble final `.gb` binary from placed sections
- Generate `.sym` file for emulators/debuggers
- Generate `.map` file showing bank usage and free space
- Built-in header fixup (checksum, logo) — no separate `rgbfix` needed, available as opt-out

---

## LSP Server

### Architecture

- Thin layer over `Koh.Core`
- Holds a `Workspace` maintaining a live `Compilation`
- On file change: update `SourceText` → reparse → new `Compilation` → push diagnostics
- All heavy lifting in the semantic model

### Features

**Parity with rgbds-lsp:**
- Diagnostics (real compiler errors, not heuristic)
- Go-to-definition / find references
- Hover (symbol info, values in all bases, instruction details)
- Completion (context-aware: instructions, directives, symbols, macros)
- Rename (semantic, cross-file safe)
- Inlay hints (constant values, macro parameters, section info)
- Signature help (macro parameters)
- Semantic tokens (derived from syntax tree)

**Beyond rgbds-lsp:**
- Bank usage visualization from linker model
- Cross-bank navigation (follow FARCALL patterns)
- Code actions (extract to macro, convert instruction forms, add missing EXPORT)
- Workspace diagnostics (full-project compilation)

### Incremental Updates

- Red-green tree enables incremental reparsing — only re-lex/re-parse changed regions
- Immutable `Compilation` means old state stays valid during recomputation — no races, trivial cancellation

---

## Technology

- **Language**: C# 14 / .NET 10
- **Parser**: Hand-written recursive descent with Pratt parsing for expressions
- **Build**: `dotnet` CLI / MSBuild
- **Testing**: TUnit (source-generated, AOT-compatible, parallel by default)
- **LSP**: VS Code LSP protocol (`Microsoft.VisualStudio.LanguageServer.Protocol` or raw JSON-RPC)
- **AOT**: Native AOT compliant — no reflection, no dynamic code generation, all serialization source-generated. Enables single-binary distribution with fast cold start.

## Performance Target

A large GBC disassembly project (216 files, ~17K definitions, 17 charmaps, ~15K charmap entries) serves as the real-world benchmark. A comparable TypeScript/Tree-sitter LSP indexes it in ~29 seconds. Koh should aim to beat this significantly — target under 5 seconds for full workspace indexing, under 100ms for incremental reparse on single-file edit.
