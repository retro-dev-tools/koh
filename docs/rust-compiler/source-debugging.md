# Source debugging migration

Status: implementation in progress; no source-debugging acceptance claim.

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
debugger-ready output or PDB/assembly identity validation.

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
