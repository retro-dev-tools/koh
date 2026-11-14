# Native assembler migration

The Rust assembler retains the C# assembler as a behavioral reference and the
C# linker as the object consumer. C# language development remains supported.

Use a line-oriented source expansion stage, Pratt expressions and table-driven
instruction encoding, with explicit sections, symbols and relocations. The
incremental C# syntax tree serves the LSP and is not required for a batch CLI.
Wrapping C# or RGBDS would not implement a native Rust assembler.

Implementation and validation order:
1. Define compatible object records; independently test KOBJ v4 and RGB9 rev13.
2. Test expression precedence, wraparound and invalid arithmetic; implement it.
3. Test documented SM83 instruction encodings and operand validation; implement it.
4. Implement source expansion, directives, symbols, sections and patch resolution
   against existing fixtures and independently written tests.
5. Preserve CLI arguments, exit codes and atomic output behavior.
6. Run Rust tests, pinned RGBDS 1.0.1 differential tests, C# object-reader/linker
   checks and representative sample ROM execution. Investigate disagreements.
7. Add Linux/Windows/macOS CI and document supported behavior and deviations.
8. Review, commit, push one branch, open one PR against master; do not merge.

Review focus: malformed source must diagnose without panic; recursive expansion
must terminate; relocations must survive placement; failed assembly must preserve
existing output; local labels must retain the definition/expansion scope.
