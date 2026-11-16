# Source debugging migration

Status: source breakpoint and machine-state acceptance implemented; complete
caller-stack unwinding and real sample debugging remain open.

The native IR already carries method identity and original IL offsets on
instructions and terminators. Emission already records instruction-boundary IL
symbols. Source information must attach to these surviving offsets without
assigning compiler-generated prologues, hidden code or neighboring methods to an
arbitrary C# line. Optimization must retain each surviving instruction's origin.

## Requirements

- Validate the Portable PDB content ID against PE debug metadata before use.
  Portable CodeView uses GUID plus directory timestamp, with Age1; GUID/age
  alone is insufficient. Validate a supplied checksum over the PDB with its
  20-byte ID zeroed, as specified by the PE/COFF addendum.
- Accept missing optional symbols; diagnose malformed or mismatched supplied
  symbols. Never read arbitrary paths embedded in untrusted PE metadata.
- Decode document changes, signed deltas, hidden points and zero/absent tables.
  Hidden points end the previous source range until the next visible point.
- Bound file size, metadata rows, expanded document strings and sequence points.
- Keep reference assembly index and method token together; identical tokens from
  different modules must never share source mappings.
- Emit source ranges through existing ELF metadata and Rust linker KDBG output.
  Synthetic code has no fabricated source line. No new object or debug format.
- Verify Debug/Release, no-PDB builds, referenced methods, malformed inputs and
  actual debugger source breakpoints, disassembly mapping and stack inspection.

## Reader choice

The maintained symbolic-ppdb13.9 reader was evaluated. Its public line cache
skips hidden points and extends preceding visible ranges across them; exact
sequence-point access is private. That cache is unsuitable as the compiler's
provenance contract. Adding the broader symbolic framework would not resolve
that semantic mismatch.

Use the existing clrmeta root/heap readers with a bounded Portable PDB adapter.
Its compressed signed integer reader uses a different negative-value rule from
ECMA-335; the adapter must sign-extend the encoded6/13/28-bit signed payload.
Fixed independently encoded boundary vectors demonstrate the difference.

Primary references: [Portable PDB metadata specification](https://github.com/dotnet/runtime/blob/main/docs/design/specs/PortablePdb-Metadata.md),
[symbolic cache conversion](https://github.com/getsentry/symbolic/blob/13.9.0/symbolic-ppdb/src/cache/writer.rs),
[symbolic cache lookup](https://github.com/getsentry/symbolic/blob/13.9.0/symbolic-ppdb/src/cache/lookup.rs).
[PE/COFF debug-directory addendum](https://github.com/dotnet/runtime/blob/main/docs/design/specs/PE-COFF.md)
defines identity, checksum and bounded embedded-PDB extraction requirements.

## Implementation sequence

1. Test bounded sequence-point decoding and signed deltas independently.
2. Parse standalone metadata/document tables and validate method ownership.
3. Match and discover adjacent/explicit symbols; bound embedded extraction if
   supported. Keep reusable library APIs separate from filesystem discovery.
4. Attach source ranges and readable method symbols to canonical objects.
5. Drive the existing debugger from Rust-owned real C# fixture tests.
6. Audit legacy source/debug assertions, SDK flags and diagnostics, then record
   exact supported and unimplemented debug features.

## Reader checkpoint

The bounded standalone reader is implemented in `src/pdb.rs`, with no new
dependency or unsafe code. It retains the20-byte ID, document names, method-row
identity, original IL offsets, source line/column and explicit hidden markers.
Lookup never borrows a source from a neighboring method or across a hidden point.
Missing Blob heaps work when no debug rows reference them. Source hashes and
local-variable custom records are not yet consumed; this is not a claim of
debugger-ready output. PDB/assembly validation is described below.

Limits:64MiB file,262144 debug rows/methods,65535 bytes per document name,
4MiB expanded document names, one million sequence points and64MiB cumulative
name/sequence blob decode work, including repeated references. Read-only review
found that output limits alone permitted repeated zero-output blob scans; the
new cumulative budget reproduces and rejects that amplification. UTF-8 separators
and a final document-change record also have independent format regressions.

Evidence: signed-decoder baseline fails independently encoded negative values;
the corrected vectors pass. Review regressions first fail4cases, then pass after
the work/UTF-8 fixes; the final-document case also fails before its fix.
`/tmp/koh-pdb-reader-reviewed-final.log` completes145/145 in11.896s, exit0:
143 compiler-library cases and two real Roslyn DLL/PDB cases,243 driver cases
intentionally unselected. The PDB subset comprises30 unit cases plus Debug and
Release fixture cases. Real `#line` documents100/200/198 and hidden code are
checked; hand-encoded metadata tests also cover method-row matching and every
truncated prefix. These are reader tests, not ROM/debugger execution evidence.

## Identity and checksum checkpoint

`PortablePdb::for_assembly` validates the parsed PDB against the assembly's raw
Portable CodeView GUID and directory timestamp, with Age1. It reads both PE32
and PE32+ through the existing object reader. It never opens the CodeView path.
Absent or ambiguous Portable CodeView identities diagnose. Multiple checksum
records may describe private/public symbol files; at least one must match when
records are present. SHA256, SHA384 and SHA512 use the specified zeroed20-byte
PDB ID, with each digest computed at most once. Unsupported algorithms and
checksum versions diagnose explicitly; no checksum record means ID-only matching.

The PE directory is limited to1024 entries,32 checksum records and65536 path
bytes. Payload bounds precede parsing and hashing. RustCrypto sha2=0.10.9 supplies
the algorithms; the compiler does not implement cryptography. New checksum and
previously missing PNG test dependency notices now cover all60 external locked
packages, with exact selected archive license texts retained.

Real Roslyn Debug/Release fixture tests validate both game and referenced
Koh.GameBoy PDBs, reject GUID and timestamp mutations, and reject a valid-format
GUID-heap mutation by checksum. Mutated real PE directories accept a mismatching
checksum before or after a matching one; all-mismatching records reject. Hand
records test ID byte order, all three algorithms, Age, duplicate/truncated
entries and unsupported checksum versions. Fixed digest vectors were calculated
independently with Python hashlib. The initial mutation test incorrectly assumed
nonempty PDB string heaps; the corrected GUID mutation preserves format validity.

Final selection154/154 passes11.857s, exit0, including39 PDB unit cases and two
Roslyn cases (`/tmp/koh-pdb-match-reviewed-final.log`). Workspace all-feature/
all-target check passes19.01s with the existing cube-helper warning; separate
rustdoc and rustfmt pass. Read-only review found no implementation defect; its
multiple-record coverage request is included. These are selected checks, not
whole-workspace or cross-platform acceptance. At that checkpoint, discovery,
emitted ELF/KDBG ranges and debugger execution were still absent; the next
checkpoint below adds discovery and ranges. Embedded extraction remains open.

## Compiler and SDK source ranges

`compile::object_with_debug` accepts optional raw PDB bytes in the resolver's
assembly order and validates each supplied PDB before use. Empty input disables
PDBs; a populated list must have one slot per assembly. Aggregate limits are
256MiB raw PDB input,16MiB expanded document names and one million points.
The plain `compile::object` API remains usable without symbols.

The emitter looks up each surviving instruction/terminator's original IL offset
in its own module's PDB. Emitted bytes acquire the current visible file/line;
contiguous equal ranges coalesce. Hidden points clear that origin. Prologues,
synthetic functions and appended arithmetic/allocation helpers have no fabricated
C# range. Zero source lines and empty document names are unavailable editor
locations; column0 uses the existing column1 fallback in ELF provenance. KDBG v1
stores file/line ranges, not source columns. Relocation diagnostics use available
provenance. Source metadata is limited to4MiB per function and64MiB per compilation.

Readable method aliases include metadata names plus module/token identity, so
overloads and equal tokens in different assemblies remain distinct. They coexist
with internal relocation symbols and are available even without PDBs. Canonical
ELF metadata and the existing Rust linker carry the ranges into KDBG; no new
container, debug format or linker path is introduced.

The CLI discovers adjacent `.pdb` files for the input and references. Missing
files are optional; malformed/mismatched files diagnose and preserve previous
output. `--pdb PATH` selects the root assembly's symbols; `--no-pdb` disables all
discovery and cannot be combined with it. Explicit and discovered PDB paths are
checked against assembly/output aliases. File paths inside PE metadata are never
opened. Embedded PDB extraction is not implemented yet.

The SDK forwards the managed PDB output path, includes present root/reference
PDBs in incremental inputs and stamps both paths and presence. `KohEmitDebugInfo`
false passes `--no-pdb` and removes stale KDBG through the existing linker boundary.
The task remains process/file orchestration only.

Focused evidence: source-range Debug/Release cases round-trip ELF, link KDBG,
preserve identical ROM bytes with/without symbols and execute the independently
expected42 in the existing emulator. Real hidden machine spans are unmapped;
both root and referenced Rgb.cs have ranges. Backend cases check unmapped
prologues/software multiply helpers and the metadata budget. These4 cases pass
11.008s. CLI optional/explicit/corrupt/alias cases pass2/2 in10.876s.

SDK Debug/Release cases pass2/2 in125.502s, including source maps for the game and
project reference, paths with spaces, PDB invalidation and debug toggling. The
first stamp expression failed MSB4012 and was corrected to an item transform.
The first timestamp probe incorrectly touched `bin`: the SDK recopies symbols
from `obj`. The corrected probe touches the intermediate PDB, checks the copied
timestamp and retains the ROM-rebuild assertion. This is not a relaxed assertion.

The checkpoint above did not establish actual debugger execution. The Rust
`driver::symbols::debugger_stops_on_native_csharp_source_and_inspects_machine_state`
cases now build real Debug/Release DLL/PDBs, round-trip canonical ELF and link
ROM/KDBG, then drive the existing C# DebugSession/DAP through a thin process host.
The ROM boots through the existing boot ROM, stops at a physical Program.cs line,
and reports that exact top-frame source line, PC/SP and readable function symbols.
Disassembly bytes are compared with the actual linked ROM. The same session then
continues to a function breakpoint at `__koh_exit`, with return42, no fault and
restored D000 SP checked through debugger register responses. No production
emulator/debugger code is replaced by the fixture.

This proves top-frame/source and machine-state inspection, not caller unwinding.
The existing StackTraceHandler scans arbitrary stack words up to FFFE; native
frames include spilled values and banked return cookies, with startup SP D000.
Those heuristic assumptions are not a compiler ABI requirement. Reliable caller
frames still need independently validated compiler/debugger integration; the
milestone remains open, including source debugging of a real game sample.

`driver::symbols::genuinely_symbol_free_roslyn_assembly_compiles_and_executes`
rebuilds the root assembly with DebugType=None and DebugSymbols=false, independently
checks PE debug records for absent CodeView/embedded PDB/checksum, compiles without
symbols and executes42 in both configurations. Its real Koh.GameBoy DLL is copied
into an isolated file reference before rebuilding, with bytes preserved and no
sidecar; the test cannot rebuild shared runtime outputs with symbols disabled.
This replaces `Gb2048KdbgTests.Frontend_DoesNotThrow_WhenAssemblyHasNoPdb`; the
sample-specific KDBG resolution method remains partially covered.

Final focused selection:10/10 passes in69.777s, exit0
(`/tmp/koh-debug-symbols-reviewed.log`);243 unrelated driver cases were explicitly
unselected. Initial fixture compilation failures (delegate conversion and an
invalid StopReason member) were fixed. Review caught the shared reference rebuild
described above; the final run uses the isolated reference and same-session resume
assertions. No tests were ignored or weakened to obtain this result.

The broader selected regression completes254/254 in416.277s, exit0
(`/tmp/koh-pdb-mapping-regression.log`), with253 intentionally unselected cases
in those test binaries. It includes compiler-library and SM83 targets, PDB/CLI
cases and actual2048/JRPG SDK gameplay. This is not a full-workspace result.
