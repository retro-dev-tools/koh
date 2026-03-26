# Koh — Binding & Emit Architecture (Phase 5)

**Date**: 2026-03-26
**Status**: Draft
**Depends on**: Phase 1-4 (parser, syntax tree, directives)

## Overview

Phase 5 takes the parsed syntax tree and produces assembled bytes. It introduces the binding layer — a two-pass walk over the syntax tree that resolves symbols, validates instructions, evaluates expressions, and emits bytes into section buffers.

The binding output serves two consumers: the linker (bytes + patches + symbols) and the LSP server (SemanticModel with symbol info, instruction encodings, diagnostics).

## Architecture: Two-Pass with Deferred Fixups

### Why Two-Pass

The SM83 has no variable-length instructions — instruction size is determined entirely by mnemonic and operand types, never by operand values. This means Pass 1 can compute exact PC values for every label without emitting bytes. Forward references (e.g., `jp main` before `main:` is defined) are resolved naturally: Pass 1 records the label address, Pass 2 uses it.

### Pipeline

```
SyntaxTree
    │
    ▼
Pass 1: SymbolCollector
  - Walk statements in source order
  - Advance PC per instruction/directive (size from InstructionTable)
  - Record label → PC, EQU → deferred expression
  - Track active section
    │
    ▼
EquResolver
  - Topological sort of EQU dependency graph
  - Evaluate constants in dependency order
  - Report cycles as diagnostics
    │
    ▼
Pass 2: Emitter
  - Walk statements again
  - Evaluate expressions (all labels now defined)
  - Pattern-match instructions against InstructionTable
  - Emit bytes into SectionBuffer
  - Record PatchEntry for linker-time references
    │
    ▼
PatchResolver
  - Re-evaluate deferred expressions (all same-file symbols now defined)
  - Apply byte patches to section buffers
  - Remaining unresolved → linker patches
    │
    ▼
BindingResult
  - SectionBuffers (bytes + remaining patches)
  - SymbolTable (for LSP and linker)
  - Diagnostics
```

---

## Symbol Table

### Symbol

```csharp
public enum SymbolKind { Label, Constant, StringConstant, Imported, Exported }
public enum SymbolState { Undefined, Defined, Resolving }

public sealed class Symbol
{
    public string Name { get; }
    public SymbolKind Kind { get; }
    public SymbolState State { get; internal set; }
    public long Value { get; internal set; }
    public string? Section { get; }           // null for absolute constants
    public SyntaxNode? DefinitionSite { get; }
    // Reference tracking for LSP find-all-references
    internal List<SyntaxNode> ReferenceSites { get; }
}
```

### Local Label Scoping

Local labels (`.loop`) are scoped to the nearest preceding global label. Internally stored as qualified names: `main.loop`. Lookup checks qualified name first, then bare name.

```csharp
public sealed class SymbolTable
{
    private readonly Dictionary<string, Symbol> _symbols;
    private Symbol? _currentGlobalAnchor;

    public Symbol? Lookup(string name) { ... }
    public Symbol DefineLabel(string name, long pc, SyntaxNode site) { ... }
    public Symbol DefineConstant(string name, long value, SyntaxNode site) { ... }
    public Symbol DeclareForwardRef(string name, SyntaxNode referenceSite) { ... }
}
```

When a global label is defined, it becomes the `_currentGlobalAnchor`. When a local label `.loop` is defined, it is stored as `{anchor}.loop`. Lookup for `.loop` tries `{currentAnchor}.loop` first.

### EQU Resolution

EQU constants may reference other EQU constants. After Pass 1 collects all definitions, `EquResolver` builds a dependency graph and resolves in topological order. Cycles produce diagnostics.

---

## SM83 Instruction Table

### Design: Declarative Data Table

The ~500 SM83 instruction encodings are represented as a static table of `InstructionDescriptor` records. This table is the single source of truth for:
- Instruction validation (reject invalid operand combinations)
- Instruction sizing (Pass 1 PC advance)
- Byte encoding (Pass 2 emit)
- Future LSP hover info (cycle counts, flag effects)

### Operand Patterns

Each syntax tree operand node maps to an `OperandPattern` value:

```csharp
public enum OperandPattern
{
    // Registers
    RegA, RegB, RegC, RegD, RegE, RegH, RegL,
    RegAF, RegBC, RegDE, RegHL, RegSP,
    // Indirects
    IndHL, IndBC, IndDE, IndHLInc, IndHLDec, IndC,
    // Immediates (width determined by value)
    Imm8, Imm16, Imm8Signed, Imm3, Imm8HiPage,
    // Conditions
    CondNZ, CondZ, CondNC, CondC,
}
```

### Instruction Descriptor

```csharp
public sealed class InstructionDescriptor
{
    public string Mnemonic { get; init; }
    public OperandPattern[] Operands { get; init; }
    public byte[] Encoding { get; init; }        // template opcode bytes
    public EmitRule[] EmitRules { get; init; }    // how to embed operand values
    public int Size { get; init; }               // total byte count
}
```

### Pattern Matching

The `OperandPatternMatcher` converts syntax nodes to `OperandPattern` values, then does a linear scan through candidates for the mnemonic. Forward references (value unknown) are assumed to fit — validated in Pass 2.

### Instruction Sizing (Pass 1)

SM83 instruction size depends only on mnemonic + operand types, never on operand values. `InstructionSizer` uses the table to determine size without evaluating expressions.

---

## Section Model

### SectionBuffer

```csharp
public sealed class SectionBuffer
{
    public string Name { get; }
    public SectionType Type { get; }
    public int? FixedAddress { get; }
    public int? Bank { get; }
    public int BaseAddress { get; }
    public int CurrentOffset => _bytes.Count;
    public int CurrentPC => BaseAddress + CurrentOffset;

    public void EmitByte(byte value) { ... }
    public void EmitWord(ushort value) { ... }  // little-endian
    public int ReserveByte() { ... }            // returns offset for patching
    public void RecordPatch(PatchEntry patch) { ... }
    public void ApplyPatch(int offset, byte value) { ... }
}
```

### SectionManager

Owns all sections, tracks active section. SECTION directives open or resume sections (validates type/address match on re-entry).

### Pass 1 PC Tracking

Pass 1 uses a lightweight `SectionPCTracker` — just a `Dictionary<string, int>` of section PCs. No byte arrays allocated until Pass 2.

---

## Expression Evaluator

Returns `long?` — `null` means "cannot evaluate yet" (forward ref or linker-time symbol).

```csharp
public sealed class ExpressionEvaluator
{
    public long? TryEvaluate(GreenNodeBase expr) { ... }
}
```

- Numeric literals → parse value from text (`$FF` → 255, `%1010` → 10, `&77` → 63)
- Identifiers → look up in SymbolTable; `null` if undefined
- Binary/unary expressions → fold recursively; `null` if either operand is `null`
- `$` (current address) → `SectionBuffer.CurrentPC`
- `HIGH(expr)` / `LOW(expr)` → evaluate inner, extract byte
- `BANK()` / `SIZEOF()` / `STARTOF()` → `null` (linker-time, produces patch)

---

## Binder Decomposition

| Class | Responsibility |
|---|---|
| `Binder` | Orchestrates Pass 1 → EquResolver → Pass 2 → PatchResolver |
| `Pass1Visitor` | Walk tree, define symbols at correct PC, track section PCs |
| `EquResolver` | Topological resolution of EQU constants |
| `Pass2Visitor` | Walk tree, evaluate expressions, encode instructions, emit bytes |
| `OperandPatternMatcher` | Syntax nodes → InstructionDescriptor selection |
| `InstructionEncoder` | Descriptor + values → byte stream |
| `ExpressionEvaluator` | Fold constant expressions, return null on forward ref |
| `PatchResolver` | Re-evaluate deferred expressions, apply byte patches |
| `SymbolTable` | Own all symbols, scope local labels, track forward refs |
| `SectionManager` | Own all SectionBuffers, open/resume sections |

---

## Patch Model

```csharp
public enum PatchKind { Absolute8, Absolute16, Relative8 }

public sealed class PatchEntry
{
    public string SectionName { get; init; }
    public int Offset { get; init; }
    public GreenNodeBase Expression { get; init; }  // AST to re-evaluate
    public PatchKind Kind { get; init; }
    public int PCAfterInstruction { get; init; }    // for Relative8
    public TextSpan DiagnosticSpan { get; init; }
}
```

---

## Output: BindingResult

```csharp
public sealed class BindingResult
{
    public IReadOnlyDictionary<string, SectionBuffer>? Sections { get; }
    public SymbolTable? Symbols { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool Success { get; }
}
```

### BoundInstruction (Semantic Model)

The binder produces `BoundInstruction` records per instruction for the SemanticModel. These carry both semantic meaning and encoding — not a separate IL, but structured enough for LSP hover/go-to-definition.

```csharp
internal sealed record BoundInstruction(
    InstructionDescriptor Descriptor,
    long?[] OperandValues,
    TextSpan SourceSpan
);
```

---

## Public API: Future Compiler Support

The following APIs are designed as public surfaces from day one, enabling future tooling (including a hypothetical higher-level language compiler) to reuse Koh's encoding and section management:

### InstructionEncoder (allocation-free)

```csharp
public static class InstructionEncoder
{
    public static int Encode(Span<byte> buffer, InstructionDescriptor descriptor,
                             ReadOnlySpan<long> operandValues);
}
```

### RomBuilder (section + symbol management)

```csharp
public sealed class RomBuilder
{
    public SectionHandle DefineSection(string name, SectionType type, ...);
    public void SetCurrentSection(SectionHandle section);
    public void EmitBytes(ReadOnlySpan<byte> bytes);
    public void EmitRelocation(SymbolHandle symbol, PatchKind kind);
    public SymbolHandle DefineSymbol(string name);
    public SymbolHandle DefineConstant(string name, int value);
    public bool TryBuild(Stream output, out IReadOnlyList<Diagnostic> diagnostics);
}
```

### InstructionTable (queryable metadata)

```csharp
public static class InstructionTable
{
    public static IEnumerable<InstructionDescriptor> Lookup(string mnemonic);
    public static bool TryGetEncoding(string mnemonic, OperandPattern[] operands,
                                       out InstructionDescriptor descriptor);
}
```

These APIs mean a future compiler can bypass the parser entirely and call `RomBuilder.EmitBytes` + `InstructionEncoder.Encode` directly. The assembler and the compiler share encoding logic and section management without needing a shared IL.

---

## Edge Cases

- **JR backward ref**: Fully resolved in Pass 2, no patch needed. Offset validated to fit [-128, 127].
- **JR forward ref**: Pass 1 gives label its address. Pass 2 computes offset. No patch needed for same-file labels.
- **JR cross-section**: Hard error — JR cannot cross section boundaries.
- **`$` in floating section**: Section-relative offset (0-based). Linker applies base.
- **DB with label ref**: PatchKind.Absolute8. Range-checked at patch resolution.
- **`[hl+]` / `[hl-]`**: Special-cased in parser (flat tokens, not expression). Pattern matcher recognizes IndHLInc/IndHLDec.
- **`c` register as condition flag**: `CKeyword` always parses as register. Pattern matcher checks context — in `JP/JR/CALL/RET` first operand position, `RegC` matches `CondC`.
