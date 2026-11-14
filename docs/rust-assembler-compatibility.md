# Assembler and linker compatibility checks

RGBDS 1.0.1 is the independent language and link-result oracle.
Current verification uses native ELF assembly and native linking alongside
separately assembled RGBDS objects and `rgblink`. Complete raw images are
compared before cartridge checksum fixups.

## Current matrix

| Check | Coverage |
| --- | --- |
| Rust workspace tests | Typed model/ELF bounds, assembler semantics, every SM83 opcode, linker placement/symbol/patch rules |
| RGBDS-enabled Rust tests | Independent instruction, expression, LOAD, alignment and relocation results |
| Python current-pipeline verifier | Execution, relocations, macros, opcode fixture, independent semantics, 2048 sample and cross-file symbols |
| Negative pipelines | Nine malformed inputs checked independently with native tools and RGBDS |
| Macro investigation script | Four reconstructed linked-byte comparisons and the explicit no-global-anchor language difference |
| Execute-only managed helper | Both independently linked execution ROMs must write 2A 2B at WRAM C000 within 10,000 instructions |
| Managed tests | Existing source/ROM/debugger assertions retained through the ELF migration; see the test disposition |

The sample's RGBDS-only temporary copy normalizes legacy macro/constant
declarations and records adaptations. Tracked sample inputs stay untouched.
The unconstrained 2048 cartridge is built separately. Koh and RGBDS choose
different valid addresses for equal-sized floating sections: Koh preserves
declaration order and RGBDS reverses those ties. For the sample's byte oracle,
RGBDS's independent map pins addresses in the temporary native source copy.
The result report identifies this constrained comparison; it does not claim
that the default floating images are byte-identical.
The ordinary test corpus is not all independently linked, executed or hardware
qualified.

## Retired historical comparison

The previous verifier used the removed C# assembler at commit
`bf0a5fc1792b6022328af080cd50f7b4d7e143c5` as a behavioral reference.
Its extracted positive raw-string Emit corpus had 233 fixtures: 183 exact
matches, 18 native rejections and 32 other differences; 50 classifications
explained intentional differences. Another 115 negative/nonasserted literals
were inventoried as excluded. These are historical measurements, not current
pass counts.

The pinned-checkout builder, C# object dump/link helper, KOBJ graph baselines and
their expected-differences JSON were retired with KOBJ. Their historical
classification mechanism compared a removed serialization graph; it is not a
valid ELF gate. Meaningful source assertions remain in the native/managed test
suites, with migration decisions in
[test-disposition.md](rust-linker/test-disposition.md). Independent semantic,
macro, negative, sample and cross-file checks remain in the current verifier.
No current tool fetches or builds that historical implementation.

These checks cover their named fixtures. They do not prove complete RGBDS
language compatibility, all placement choices, graphical correctness or
physical hardware operation.
