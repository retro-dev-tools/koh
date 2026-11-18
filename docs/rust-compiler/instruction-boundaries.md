# Instruction decoding boundaries

This boundary batch replaces four of the19 `Backends/MirDecoderTests.cs` methods.
The other15 now have separate [executable-effect replacements](machine-effects.md).
No legacy MIR model is recreated, and byte decoding alone does not certify effects.

## Independent contract

The [Pan Docs encoding tables](https://gbdev.io/pandocs/CPU_Instruction_Set.html)
define base/CB families, immediate widths and the11 invalid base opcodes. Native
Rust driver tests declare lengths independently of Koh's instruction table,
serialize canonical ELF objects, round-trip them through the shared ELF reader,
and use the Rust linker to produce cartridges and KDBG. The existing debugger's
disassembly API must return exact instruction bytes, nonempty text, the exact
next address, and a one-byte unknown instruction for each invalid opcode.

Both256-entry encoding spaces are checked, including a trailing NOP at each
declared boundary. A separate12-instruction mixed stream preserves all original
bytes and offsets. `STOP 00` uses the conventional two-byte disassembly encoding;
this is not an execution claim about STOP's hardware-dependent behavior.

The compiler reads CIL, not arbitrary SM83 byte streams. It has no mutable
post-emission decoder/peephole pass requiring an opaque all-register clobber
record. At its actual input boundary, reserved CIL opcodes must be rejected.
The native opcode test uses the .NET SDK's218-entry encoding catalog, checks
every proper truncation of every catalog instruction, exact operand kinds and
next offsets, and reserved base/FE opcodes. A truncated prefix must produce an
error, never an invented value, successful omission or panic. The `no.` prefix
is handled separately from the catalog, as already documented in that test.
The old private formatter's one-byte opaque-CB representation is intentionally
not reproduced. The debugger reads a complete address space, not a bounded
machine-code slice; inventing a truncation rule for it would test a nonexistent
production interface.

## Source dispositions

All four decisions are REPLACE, discoverable by cargo-nextest. The four selected methods
contain15 lexical assertion sites; family loops expand them into more checks.

| Source method | Requirement and independent basis | Native location and replacement |
|---|---|---|
| `Decode_RoundTripsToOriginalBytes` | Preserve exact emitted bytes and contiguous ISA boundaries; public debug contract, private MIR serialization retired. | `tests/driver/symbols.rs::sm83_disassembly_preserves_mixed_stream_bytes_and_offsets`: all20 bytes,12 instructions and every offset. |
| `Decode_IsTotalOverEveryOpcode` | All base/CB encodings have explicit disassembly boundaries; ISA tables rather than legacy decoder. | `symbols::sm83_disassembly_covers_every_encoding`, two rstest cases:512 vectors, exact lengths/bytes/next addresses and invalid-opcode classification. |
| `Decode_IllegalOpcodeIsOpaqueBarrier` | Unknown input cannot silently become valid code; private all-register/all-flag MIR clobber masks retired with that model. | Same exhaustive debugger cases identify all11 invalid base encodings; `tests/opcodes.rs::encodings_match_the_dotnet_opcode_catalog` rejects reserved CIL at the real compiler boundary. Illegal SM83 opcodes are not executed. |
| `Decode_TruncatedTrailingCbDoesNotThrow` | Bounded input must not read beyond its end or panic; private opaque-CB formatter is not a native contract. | `tests/opcodes.rs` now rejects every proper truncation of all218 catalog encodings; `cil::tests::diagnoses_malformed_il` retains explicit truncated-FE and operand diagnostics. |

## Verification

Focused nextest passed43/43, exit0, in10.504s, with683 explicitly unselected
cases (`/tmp/koh-isa-boundaries-final.log`). This includes all native CIL decoder
unit cases, the .NET opcode catalog and nine debugger instruction-boundary
cases. No retries, ignored cases, new production decoder, emulator changes or
new dependencies were added. Workspace discovery lists1906 cases, including1729
compiler cases, zero ignored (`/tmp/koh-isa-boundaries-discovery.json`). The existing
cubes `support::build_host` dead-code warning remains.

```sh
KOH_DOTNET=/path/to/dotnet cargo nextest run -p koh-compile \
  --lib --test driver --test opcodes --locked \
  -E 'test(sm83_disassembly) | test(cil::tests::) | binary(opcodes)'
```

These results do not establish execution effects, optimizer legality, complete
compiler migration, or current-head three-platform acceptance. The other15
source methods now have separately verified [observable execution coverage](machine-effects.md);
the43-case result above predates that later batch.
