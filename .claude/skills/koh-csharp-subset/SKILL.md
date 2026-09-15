---
name: koh-csharp-subset
description: Which C# constructs the Koh SM83 backend can lower to a ROM. Use before writing or porting a game's C# source, or when diagnosing an out-of-subset compiler diagnostic.
---

# "Koh C#" is standard C# — a subset by what the backend can lower, not by parser rules

There is no Koh-specific syntax or Koh-specific typing rule left: a game is an ordinary C# project a
plain `csc`/Roslyn build already accepts (`AllowUnsafeBlocks=true`, nothing else nonstandard), and the
CIL frontend lowers WHATEVER IL that produces. Arithmetic promotion, operator overload resolution,
overload/generic-method binding, `switch` pattern lowering — all of it is Roslyn's, decided before the
CIL frontend ever runs. What makes a program compile to a ROM is purely whether the SM83 backend can
lower the resulting IL shapes; an out-of-scope construct is a diagnostic, not a parse error.

Supported: `byte`/`sbyte`/`ushort`/`short`/`int`/`uint`/`long`/`ulong`/`Int128`/`UInt128`/`bool`
(full arithmetic including mul/div/rem/shift at every width — i8/i16 via register routines, i32/i64/
i128 via generic width-N memory routines; i64/i128 have no register room so they return via
`Sm83Backend.ReturnScratch`),
`char`/`string` (ASCII only — non-ASCII is a diagnostic; a string is a length-prefixed ROM blob pointer,
so `.Length`/indexer/`foreach` work through parameters, fields and ctor args), `enum` (custom base), `const`,
pointers (`T*` incl. arithmetic/`++`/compare/casts, `*(T*)addr` MMIO, and `stackalloc T[n]` frame
buffers — ordinary C# unsafe code, so every containing method/type needs the real `unsafe` keyword,
unlike the deleted frontend's own parser which never required it), the `Gb.*` memory regions
(`Gb.Vram`/`Gb.TileMap`/… — `[KohIntrinsic("region", addr)]`-tagged properties on `Koh.GameBoy.Gb`,
constant base pointers on a ROM), fixed arrays (local + static ROM/WRAM data), value-type `struct`s
(nested, arrays-of, whole-copy, `ref`-passed); reference-type `class`es (heap-allocated via the `Mem`
arena, instance fields + instance methods with `this`; inheritance (derived fields laid after the base)
and `virtual`/`abstract`/`override` via closed-world dispatch — a traceable receiver devirtualizes, an
untraceable one lowers to a type-tag `switch` of direct calls (`CilVirtualDispatch`), which is what
`Scene`/`Game.Run` need; interfaces do NOT take part: an interface call is fine only when the receiver's
concrete type is traceable, an untraceable interface call is a diagnostic; a class type also names fields —
including of its own type, so linked structures work — parameters, and returns, all as heap pointers;
an instance is usable as a value/`byte*` (`return this;`), and assignment copies the reference, not the
bytes); dynamic allocation (`Mem.Alloc`/`Mem.Reset` are `[KohIntrinsic("alloc"/"heapreset")]`;
`Mem.Copy`/`Mem.Fill` are ordinary compiled `Koh.GameBoy` code (`Mem.cs`), lowered on demand like any
other referenced-assembly method — not appended/hand-written; forward copy, overlap defined only when
destination < source, count==0 a no-op, NOT vblank-aware — caller's responsibility like
`Cgb.CopyToVram`); generic methods (monomorphized — specialized per concrete type argument,
transitively via `CilMethodLowerer.Generics.cs`'s `CilGenericSubst`, which substitutes Cecil's own
`GenericInstanceMethod` type arguments directly — no syntax-tree rewriting, since Cecil already exposes
the concrete types at the call site); array LINQ reductions (`Where`/`Select` pipelines ending in
`Sum`/`Count`/`Any`/`All`, plus `Max`/`Min` directly on an array, compiled to a loop with inlined
lambdas — matched off the BCL `Enumerable`/lambda IL shape, not source syntax); cooperative coroutines
(a linear run of `yield return`s, or a single counted `for` loop with one `yield`, lowered from the
C# compiler's OWN generated state-machine class — the frontend recognizes and re-lowers Roslyn's
iterator boilerplate rather than building its own);
`if`/`while`/`do`/`for`/`switch`/`break`/`continue`/`return`; arithmetic/bitwise/shift/compare/`~`,
`&&`/`||`/`?:`/`++`/`--`, compound assignment, STANDARD C# (ECMA-334) usual-arithmetic conversions on
mixed signed/unsigned (int promotion applies — `byte * 16` no longer wraps mod 256 the way the deleted
frontend's own narrower rule did; this is a deliberate, load-bearing behavior change, not a bug); a
program written as top-level `static class`es (their static methods lower to `Class.Method` functions;
static fields become program-scope statics; the entry is whichever method is actually named `Main`) —
delegates and closures (a capturing lambda's compiler-generated display class, lowered like any other
class); `static` fields (WRAM/ROM/const) plus a `.cctor` for non-trivial static initializers/ROM array
data; `ref`/`out`/`in`; a `Hardware` register surface (`[KohIntrinsic("register", addr)]`-tagged
properties on `Koh.GameBoy.Hardware`) and `[Interrupt("VBlank")]` handlers (matched by the attribute
type's simple name — `Koh.Compiler` never references `Koh.GameBoy`); recursion (direct and mutual; a
recursive program moves the CALL stack into WRAM so it runs hundreds of levels deep, and
`rt.pushframe` traps on a stack/heap collision rather than corrupting memory); and `float`/`double`
arithmetic, routed through `[KohRuntime(key)]`-tagged `Koh.GameBoy.SoftFloat` routines rather than
inline codegen; struct return by value (hidden sret pointer — static/instance/factory/generic/recursive
returns, and pattern-based `foreach` over a struct enumerator); stored delegates (`Action`/`Func` through
ctor args, fields, parameters, returns — materialized as a 3-byte arena blob and invoked via a
closed-world `switch` over `CilDelegateRegistry`); length-carrying 1-D arrays (`.Length` survives
parameters, fields and returns — `[u16 len]` header before the payload); rank-2 rectangular arrays
(`T[,]`, `[u16 d0][u16 d1]` header, ROM-folded for `static readonly` literals); reference-element arrays
(`string[]`, class arrays). Calls into the BCL (e.g. `List<T>`, `Console`) are a diagnostic. Out by design: 128-bit+ float, reflection, unbounded/dynamic allocation patterns the
backend can't statically size. Out-of-subset constructs are reported as diagnostics, never silently
miscompiled.
