# Rust assembler migration

Prior SDL3 session remains documented in `docs/sdl3.md` and the baseline history.
That session's final whole-solution run was stopped by the user; its historical
validation claims are not renewed by this assembler work. No SDL changes are
part of this migration. Original checkout remains on feat/sdl3-renderer.

Base: origin/master bf0a5fc. Original checkout and screenshot edit are untouched.

One Cargo crate at `rust/koh-asm`, library `koh_asm`, executable `koh-asm`.
Independent implementation units:
- objects: `model.rs`, `object.rs`: section/symbol/patch records and both serializers.
- expressions: `expr.rs`: parsed expressions, constant evaluation, relocation representation.
- encoding: `encode.rs`: complete documented SM83 instruction encoding.
- lead: expansion, assembler driver, CLI, integration, differential checks and documentation.

Interfaces:
`expr::Expr`: `Number(i32)`, `Symbol(String)`, `Pc`, `Unary(String, Box<Expr>)`, `Binary(String, Box<Expr>, Box<Expr>)`, `Call(String, Vec<Expr>)`.
`expr::parse(&str) -> Result<Expr,String>`.
`expr::eval(&Expr, &dyn Fn(&str)->Option<i32>, pc:i32) -> Result<i32,String>`.
`encode::encode(mnemonic:&str, operands:&[String]) -> Result<Encoded,String>`.
`Encoded { bytes:Vec<u8>, patches:Vec<OperandPatch> }`; `OperandPatch { offset:usize, width:u8, relative:bool, expression:String }`.
`model::Object { sections:Vec<Section>, symbols:Vec<Symbol> }` (Default).
`Section { name:String, kind:u8, address:Option<i32>, bank:Option<i32>, align:u8, align_offset:i32, data:Vec<u8>, patches:Vec<Patch>, lines:Vec<Line> }` (Default).
`Symbol { name:String, kind:u8, visibility:u8, section:Option<usize>, value:i32 }`.
`Patch { offset:usize, kind:u8, pc_after:i32, pc_offset:i32, expression:Expr, file:String, line:u32, symbol:Option<String>, addend:i32, shift:i32 }`.
`Line { offset:usize, count:usize, file:String, line:u32 }`.
`object::write_kobj(&Object)->Result<Vec<u8>,String>` and `write_rgbds(&Object)->Result<Vec<u8>,String>`.

Verification: each unit owns module-local unit tests. Do not run Cargo builds concurrently in the shared target directory; use `CARGO_TARGET_DIR=/tmp/koh-rust-<unit>` for isolated unit checks. Lead runs final Cargo and C# integration checks. RGBDS reference pinned to 1.0.1. No commits or pushes by unit agents.
