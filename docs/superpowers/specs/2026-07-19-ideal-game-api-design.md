# Ideal-Code-First Game API — 2048 as the Spec (v1)

Program design doc. Grounded in `samples/gb-2048-v2/*` (the normative artifact — see §1),
`src/Koh.GameBoy/Graphics/*` + `docs/superpowers/specs/2026-07-15-graphics-library-design.md` (the
substrate this sits on), and the CIL frontend/SM83 backend sources cited per enabler in §4. Prior
design docs for this effort iterated abstractly; this one fixes the methodology the effort settled
on: **the game code is the spec**.

---

## 1. METHODOLOGY

Write the ideal game first — standard, modern C#, zero `unsafe` at the public level, no compiler
sympathy — check it in as a non-building sample, and drive the framework API and the compiler until
that sample compiles **unmodified** to a ROM and runs. The API is derived from real game code, not
designed in the abstract; a construct earns compiler work by appearing in the game.

- **Normative artifact:** `samples/gb-2048-v2/` — 2048 with scene classes
  (`TitleScene`/`PlayScene`/`EndScene : Scene`, `Game.Run(new TitleScene())`), a `Board` class with
  an indexer and a `Line` struct returned by value, `TileAsset.Define(art)` with no count,
  `Input.Repeated`-driven sliding, `Rng` spawns, scene state through constructors (including a
  `string` field). Excluded from `Koh.Ci.slnf` until it builds (final milestone).
- **Acceptance:** the sample compiles unmodified, links, boots in `GameBoySystem`, and passes a
  scripted-joypad play test (title → slides → merges → score → game over), Debug and Release.
- The raw layers (`Hardware`, `Gb`, `Mem`, `Hal/*`, `Graphics/*`) stay public escape hatches; the
  framework sits strictly on `Graphics` and re-derives none of its VRAM/OAM write-safety model. The
  sample's one raw touch — `Rng.Mix(Hardware.DIV)` — is deliberate proof of the hatch.

## 2. WHAT THE GAME CODE DEMANDS

| Construct in gb-2048-v2 | Needs | Milestone |
|---|---|---|
| `Input.Pressed/Repeated`, `Rng.Next/Chance/Mix`, `TileAsset`, frame bracketing | Framework code only — lowers today | M1 |
| `Line ReadLine(dir, i)` struct return; `Line line = default`; struct indexer | E1: struct return by value (sret) | M2 |
| `class TitleScene : Scene`, `override Enter/Update/Exit`, `Game.Run/ChangeScene` | E2: derived-field layout fix + closed-world devirtualization | M3 |
| `TileAsset.Define(byte[] data)` — `.Length` through a parameter | E4: length-carrying arrays | M4 |
| Indexers, auto-props (`private set`), `switch` expressions, ctor field init, readonly fields, `string` field via ctor | Nothing — standard C# already lowered by the CIL frontend | — |

## 3. THE FRAMEWORK (`src/Koh.GameBoy/Framework/`, `namespace Koh.GameBoy.Framework`)

Matches the `Koh.GameBoy.Graphics` precedent: folder + namespace segment, `unsafe` allowed inside,
never in a public signature; no SDK/csproj changes (the CIL frontend lowers Koh.GameBoy.dll on
demand). Cost discipline: hot accessors are single-block leaf methods (≤16 IR) so the inliner erases
them; framework per-frame state is a handful of WRAM static bytes; nothing allocates per frame.

- **`Game`** — `Run(Scene first)`: `Boot()` (Video.Init + Input reset + `Rng.Seed(Hardware.DIV)` +
  Clock zero), `first.Enter()`, `Video.Start()`, then forever: `scene.Update()`; `EndFrame()`
  (= `Video.EndFrame()` → `Input` latch → `Clock.Frames++` → commit pending scene change:
  `old.Exit(); next.Enter()`). `ChangeScene(Scene next)` is deferred to the frame boundary so a
  transition mid-`Update` is atomic. `Boot`/`EndFrame` are public for games that want to own the
  loop before M3 lands (and after — the framework never *requires* scenes).
- **`Scene`** — `abstract class` with `virtual Enter()`, `abstract Update()`, `virtual Exit()`.
  Scenes are ordinary arena classes; state flows through constructors. One devirtualized dispatch
  per frame.
- **`Input`** — latched once per frame over `Joypad.ReadAll()`: `Held/Pressed/Released/Repeated
  (Button)`, `SetRepeat(byte delayFrames, byte intervalFrames)` (default 15/4), `DpadX()/DpadY()`
  (−1/0/+1). Backing: `_held`/`_pressed = cur & ~prev`/`_released = prev & ~cur` static mask bytes +
  one shared repeat timer reset when the held mask changes. Fixes `Joypad.Pressed()`'s consume-once
  hazard (that API stays for Graphics-level users).
- **`Rng`** — 16-bit xorshift (`s ^= s<<7; s ^= s>>9; s ^= s<<8` — byte-friendly shifts on SM83):
  `Seed(ushort)` (0 coerced nonzero), `Next()` (high byte), `Next(byte maxExclusive)` (modulo; bias
  ≤1/256, documented), `Next16()`, `Chance(byte per256)`, `Mix(byte entropy)` (xor into state).
- **`Clock`** — `uint Frames` (the byte-wide `Video.FrameCount` wraps in 4.3 s). **`Timer`** —
  embeddable struct: `Start(ushort frames)`, `Tick()` (true exactly on reaching 0), `Running`.
- **`TileAsset` / `MapAsset`** — ROM asset handles binding data + count once at the declaration:
  `TileAsset.Define(byte[] data)` (E4; until then an explicit-count overload),
  `Load(byte baseTile)` (via `TileSet.Load`), `byte TileCount`, `Tile(byte i)`;
  `MapAsset.Define(cells, w, h)` + `Draw(col, row)` (via `Bg.DrawMap`).

Explicitly deferred (ride the same enablers later, 2048 doesn't need them): `Actor`/`Actors`
sprite-backed entities, 8.8 `Fixed` with operators, stored delegates/events (E3), generic types
(E5), metasprites, camera, audio, asset pipeline.

## 4. COMPILER ENABLERS (feasibility verified against source)

### E1 — struct return by value, lowered as a hidden `sret` parameter (M2)
The ban is two diagnostic sites: `CilLoweringContext.EnsureSignature` and `BuildGenericSignature`.
The frontend already represents every struct value as *the address of its bytes*
(`CilMethodLowerer.Structs.cs`), so: `Line f(...)` → `void f(..., i8* sret)`; callee `ret v` →
`EmitCopy(sret, v, size)`; each call site allocas an `Array(I8, size)` frame buffer, passes its
base, and pushes that address as the result value — exactly what `stloc`/`stfld`/`PrepareArg`
already consume. **Zero IR/verifier/optimizer/backend changes.** Explicitly *not* via the backend's
`ReturnScratch` (shared, non-reentrant — `CheckNoInterruptReentrancy` would reject interrupt+struct
programs; and the IR has no aggregate value type: `IrType.SizeInBytes` throws for `Struct`). The
convention composes with recursion (sret is an ordinary pointer arg through `ArgScratch`), banking
(callee is void — `EmitThunk`'s restore discipline untouched), and interrupts (buffer in caller's
frame). Bonus verified: Roslyn binds `foreach` structurally, and struct instance methods are
non-virtual — so `foreach` over a concrete struct enumerator needs E1 and nothing else.

### E2 — class inheritance + closed-world devirtualization (M3)
Two parts, ordered:
1. **Layout correctness fix (standalone):** `CilClassLayout.Compute` walks only `type.Fields` —
   derived-class fields are laid out from offset 0 and silently overlap the base's. Prefix layout
   (base chain first), plus a reserved tag byte at offset 0 of hierarchy roots that need dispatch.
2. **Devirtualization:** a pass-0 scan (pattern per `CilStaticFieldSupport.Collect`) enumerates
   non-sealed hierarchies with virtual/abstract methods and assigns dense byte tags; `LowerNewobj`
   stores the tag after zero-fill; `LowerInstanceCall`'s currently-diagnostic virtual branch
   (`CilMethodLowerer.Delegates.cs`) becomes tag load → `IrBuilder.Switch` → one **direct** call
   per arm via the existing `ResolveOverride`, non-void results merged through an alloca slot
   (preserves the frontend's no-phi invariant; `Mem2RegPass` cleans up). The existing
   `_pendingConcreteType` fast path stays for statically-known receivers.

**Rejected: a true indirect-call IR op.** Every backend soundness consumer is keyed to direct
`CallInstruction`s — Tarjan recursion detection + interrupt-reentrancy (`BuildCalleeGraph`),
dead-function pruning, `InliningPass`, and banking's thunk routing; arguments are written into
callee-specific static WRAM slots (`EmitCall`); and function addresses in data have no fixup
mechanism (data sections carry no patches). A banked indirect target would need thunk-address
indirection on top. Tag + switch keeps every target a literal direct call: all analyses stay sound
with zero backend changes, and SM83 switch cost is trivial at per-scene/per-actor granularity.
Revisit only if dispatch is ever *measured* hot.

### E4 — length-carrying arrays (M4)
Arrays have no length header; `.Length` resolves only through compile-time provenance
(`_pendingArrayInfo`) and is a diagnostic through a parameter (`LowerLdlen`). The mechanism already
ships for strings: `[u16 len][bytes]` ROM blobs (`EnsureStringLiteralGlobal`). Apply it to `T[]`
with the reference pointing at the **payload** (base+2): element geps, `Gb.*` interop, and
`Mem.Copy` are untouched; heap `newarr` allocates size+2 and stores the count; ROM folding
(`CilStaticFieldSupport`/`Statics.cs`) prepends 2 bytes; `LowerLdlen` becomes a load at base−2.
Provenance tables are kept as a constant-folding fast path (protects LINQ lowering's cycle budgets).

## 5. MILESTONES

Each lands committed and green (`dotnet build Koh.Ci.slnf` warning-free; fixtures through the real
pipeline: Roslyn → `CilFrontend` → `IrVerifier.Verify(module).IsEmpty()` → SM83 → link →
`GameBoySystem` asserts, Debug **and** Release) before the next starts.

- **M0** — this spec + `samples/gb-2048-v2` checked in (non-building, excluded from CI); spike
  fixture proving `string`-field-through-ctor flow (the one standard-C# construct in the sample
  without existing test coverage).
- **M1** — Framework Stage 0: `Input`, `Rng`, `Clock`/`Timer`, `TileAsset`/`MapAsset`
  (explicit-count `Define` until M4), `Game.Boot`/`EndFrame`. Tests:
  `Frontends/CilFrameworkTests.cs` (scripted-joypad edges/repeat, Rng sequence + desktop parity,
  Timer semantics, asset loads asserted against VRAM).
- **M2** — E1 sret. Fixtures: struct-returning statics/instance methods/factories, nested calls,
  recursion, struct-enumerator `foreach`.
- **M3** — E2 layout fix (own commit + regression fixture), then devirtualization; land `Scene` +
  `Game.Run`/`ChangeScene`. Fixtures: multi-subclass dispatch, scene-transition timing
  (`ChangeScene` deferred to frame boundary; `Enter`/`Exit` ordering), interrupt coexistence,
  banked-program smoke.
- **M4** — E4 length-carrying arrays; count-free `TileAsset.Define`. Fixtures: `.Length` through
  params/fields/returns, ROM + WRAM arrays; existing array/LINQ suites stay green.
- **M5** — `samples/gb-2048-v2` joins the build; `Samples/Gb2048V2Tests.cs` scripted-play
  acceptance; residue diagnostics fixed. `gb-2048-cs` stays as-is (retirement is a separate
  decision).
