# CPU reference fixture

`oracle.bin` contains 2048 independently executed C# `Sm83` instructions:
all 256 unprefixed and 256 CB-prefixed opcodes with four register/flag inputs.
Each record preserves the complete 26-byte CPU state and the observed memory
read/write addresses, values and T-cycle timestamps. The Rust test consumes
literal reference reads and checks writes, timing and final state. This isolates
CPU parity; it does not establish MMU or whole-machine parity.

`oracle-capture.cs` is the exact independent capture program. Run it in a .NET
10 console project referencing `src/Emulator/Koh.Emulator/Koh.Emulator.csproj`,
passing an output directory. Then pack the emitted JSONL:

```sh
python3 rust/koh-emulator/src/cpu/pack_oracle.py /tmp/cpu-oracle/cpu.jsonl rust/koh-emulator/src/cpu/oracle.bin
```

SHA-256 at capture:

| Artifact | SHA-256 |
| --- | --- |
| cpu.jsonl | 9d92136865b793993a5ad6dba39dbc7fe36b9c54ad7c74f2a10d69c0e8df6e3a |
| oracle-capture.cs | 4f2d73992af3154aae37f9e9162015dfe0813c0e0c45cf15aa5d95e537f7a253 |
| CpuRegisters.cs | 83aeb9946e1e28563436ff3c37f949f139f1e7e6ac05a3039deb23e629136dcc |
| InstructionTable.cs | 4dce8d60aa125d779f3a10bf2d695998c1f2b0abdb851c89fd7f68955527b3bd |
| Interrupts.cs | e6a3315221e31ab516c17d2e558af2c69e7603a54c55cd1b3c084a4d917b4e93 |
| Sm83.cs | d35a54181b2ba3330ed95f50ad0e36da6ff25d7d7a9a6578efad14c887023685 |

Binary layout: LE u32 record count; each record has u8 prefix/opcode/variant,
26 raw state bytes, u8 trace count; each trace has u8 write flag, LE u16 address,
u8 value, LE u64 elapsed T-cycles. No ROM bytes or runtime dependency is embedded.

`Sm83InstructionTests` and `InterruptAndHaltTests` migrated coverage lives in
`cpu.rs`: immediate and ALU flags; pair loads and wrapping; calls/returns and
stack order; all opcode cycles and conditions; all CB values/flags; EI delay,
DI cancellation, immediate RETI, HALT bug and clean wake; interrupt stack IE
clobber; STOP padding/speed-switch decision. Instruction fixture coverage also
preserves existing LDH, LDI and arithmetic cases independently.
