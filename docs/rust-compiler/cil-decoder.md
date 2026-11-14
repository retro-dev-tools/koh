# Native CIL encoding decoder

`koh_compile::cil::decode` reads instruction boundaries, operands and relative
control-flow targets. This is not a claim of semantic support for every decoded
instruction. Typed stack verification, exception handling decisions, token
binding, prefix legality and lowering still belong to later compiler stages.

The reader preserves byte offsets in the original method body, signed integer
constants, floating-point bit patterns and raw metadata tokens. Branches are
relative to the byte after their operand; every switch displacement is relative
to the end of the whole switch table. Every target must identify an instruction
boundary within the method. Malformed/truncated/unknown encodings report an
`IL_xxxx` offset; they do not become no-ops or placeholder values.

Method bodies are limited to 1 MiB before allocating instruction/target vectors.
Switch sizes are checked against remaining input before allocation. No input
assembly is executed by this production code.

## Independent checks

Rust parameterized tests check signed values, preserved NaN payloads/signed zero
and infinity bits, original offsets, short/long branches, switch-table bases,
invalid targets, malformed operands and resource boundaries. Real Roslyn Debug
and Release fixtures are decoded too, including the *encoding* of exception
clauses' leave instructions; exception execution is not implemented.

The Rust `opcodes` integration test builds and executes a separate, test-only
.NET reflection tool that reads `System.Reflection.Emit.OpCodes`. It compares
operand kinds and widths for all 218 nonreserved catalog entries, and checks the
complementary reserved encoding set. It does not execute the game fixture or use
the old Koh compiler as an oracle. ECMA's `no.` prefix (`FE19`) has an explicit
encoding case even though it is absent from that runtime catalog. Recognizing
the prefix's bytes does not authorize suppressing null/type/range checks.

Reference: [ECMA-335 Partition III](https://ecma-international.org/publications-and-standards/standards/ecma-335/)
and the [.NET runtime opcode definitions](https://github.com/dotnet/runtime/blob/main/src/coreclr/inc/opcode.def).
