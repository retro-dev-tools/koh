# Koh.GameBoy Graphics Library — API Design (v1)

Design doc only; no library code written. Grounded in `src/Koh.GameBoy/Hal/*`, `Hardware.cs`, `Gb.cs`, `Mem.cs`, `samples/gb-2048-cs`, `samples/gb-3d/*`, and the CIL-frontend constraints in `docs/superpowers/specs/2026-07-14-cil-frontend-design.md`.

---

## 1. SCOPE

**v1 — one focused layer, `Graphics/`, that makes both existing samples writable without touching a hardware register.** Eight modules:

| Module | What it gives you | Why it's in v1 (evidence) |
|---|---|---|
| `Video` | Boot choreography (LCD off → author → on), layer toggles (BG/sprites/window), scroll, `EndFrame()` frame pacing + flush point | Every sample hand-orders `Lcd.Off/SetPalette/Scroll/On` and open-codes `while(true){...; Ppu.WaitVBlank();}` |
| `TileSet` | Bulk 2bpp tile loading (LCD-off fast path, vblank-chunked live path), 1bpp expansion | 2048's `GenerateTileset` hand-rolls 8×`TileData.SetRow` per tile; every gb-3d variant hand-chunks VRAM copies |
| `Bg` / `Win` | Tilemap drawing on both maps: `SetTile`, `Fill` (rect of one tile), `DrawMap` (blit a ROM rect), CGB per-tile attributes; window enable | `RenderBoard`'s nested 2×2 loops; `Gb.TileMap1`, `WX/WY`, and VRAM-bank-1 attributes are completely unwrapped today |
| `Font` / `Text` | Built-in ROM ASCII font, `Draw(col,row,"SCORE")`, `DrawNumber` | Neither sample can render a score at all — a confirmed hole, not a refinement |
| `Sprites` | 40-entry shadow OAM + `Sprite` handles (`Move/SetTile/Hide`), flushed automatically by `Video.EndFrame()` via hardware OAM DMA | Zero C# code touches `Gb.Oam` today; the single biggest gap all three recons agree on |
| `Palettes` / `Rgb` | One palette API for DMG shades **and** CGB RGB555, BG **and** OBJ, auto-dispatch on `Cgb.IsColor()` | `Lcd.SetPalette` (DMG BG only) and `Cgb.SetBackgroundColor` (CGB BG only) leave OBJ coloring impossible on both variants |
| `Canvas` | Tile-backed pixel surface: `SetPixel/FillSpan/FillRect/DrawLine/FillTriangle/Clear/Present()`, single- or double-buffered, DMG-chunked vs CGB-GDMA present | The three near-identical ~100-line `Surface.cs` files + `SpanFill.cs` **are** this module, copy-pasted; v1 here is consolidation of proven code, not invention |
| `Joypad` additions | `ReadAll()` (all 8 buttons — A/B/Select are unreachable today), `Pressed()` rising-edge | 2048 hand-computes `(held ^ previous) & held`; additive, doesn't touch existing `Read/Held` |

**Canvas is in v1, deliberately.** The task serves game writers *and* demo writers; the demo half already exists as tuned, hardware-verified sample code duplicated 3×. Cutting it from v1 means gb-3d stays low-level and the library only serves half its audience. It is scoped down: one canvas per program, fixed at `Init`, no arbitrary blitting, no interaction with `Sprites`/`Bg`.

**Later (explicitly out of v1):** LYC/STAT raster-effect hooks (racing-beam's SCX wobble stays hand-rolled — it's a per-scanline effects framework, a design of its own); metasprites (multi-OAM actors) and 10-sprites-per-scanline management; camera/map streaming for >32×32 worlds; animation/tween helpers; an asset pipeline (PNG→tiles MSBuild task); custom fonts; `Rng` helper (one function, adjacent not graphics — bundle with the demo if convenient).

---

## 2. ARCHITECTURE

**Location:** `src/Koh.GameBoy/Graphics/` — new folder beside `Hal/`, flat `namespace Koh.GameBoy` (matching the Hal precedent: folder for organization, one namespace for game code — a game keeps its single `using Koh.GameBoy;`). Because the CIL frontend lowers Koh.GameBoy.dll on demand, adding these types requires **no SDK or project-file changes** — every game gets them by referencing Koh.GameBoy, and the same code runs on the desktop reference build.

**Layering (strict, no duplication):**

```
Game / Demo code
   │
Graphics/   Video · TileSet · Bg · Win · Font · Text · Sprites · Palettes · Canvas
   │  calls, never re-derives:
Hal/        Ppu (all timing waits) · Cgb (IsColor, CopyToVram, VBK) · Lcd · Tilemap · TileData · Joypad
   │
Hardware.cs registers + Gb.cs region pointers + Mem.cs arena
```

Rules: all PPU-mode reasoning goes through `Ppu.WaitVBlank/WaitForVramAccess/WaitForHBlank` — Graphics never spins on `STAT`/`LY` itself. `Lcd/Tilemap/TileData` stay public as the escape hatch; Graphics uses them internally where they fit (`Tilemap.SetTile` for single cells, `TileData` for row pokes) and goes to `Gb.*` pointers directly for bulk loops. `Cgb.IsColor()` is called once in `Video.Init()` and cached as `Video.IsCgb`.

**Prerequisite plumbing (small, called out explicitly):**
1. `Hardware.cs` additions — `DMA` ($FF46), `OBP0`/`OBP1` ($FF48/49), `OCPS`/`OCPD` ($FF6A/6B), `LYC` ($FF45, cheap, unblocks "later" raster work). Trivial `[KohIntrinsic("register", addr)]` entries; the desktop host side must also emulate an $FF46 write (copy page → `Gb.Oam`) so the reference build renders sprites.
2. **One compiler intrinsic:** `Hardware.RunOamDma()` (`[KohIntrinsic("oamdma")]`). OAM DMA locks the bus to all but HRAM for ~160 M-cycles, so the trigger+wait loop must execute from HRAM — inexpressible as compiled Koh C#. The backend emits a fixed HRAM trampoline once (same "compiler-owned" pattern as `alloc`/`heapreset`); the desktop implementation is just the page copy.
3. **One compiler attribute:** `[KohAligned(n)]` on static fields, honored by the static-WRAM allocator (desktop: over-allocate + align). Needed because OAM DMA requires a page-aligned (0xXX00) source and HDMA requires 16-byte alignment — neither a plain static array nor `Mem.Alloc` guarantees either today; `Surface.cs` hand-aligns with `ulong` rounding, which the library must not perpetuate.

**Write-safety stance — decided per module, stated in every doc comment (no "caller's responsibility" ambiguity):**
- **Immediate-checked:** `Bg`/`Win`/`Text` map writes and `Palettes` writes internally gate on `Ppu.WaitForVramAccess()` (single-byte/short-burst writes; mode-0/1 windows are plentiful). Matches how the samples already use `Tilemap.SetTile` safely.
- **Deferred-to-EndFrame:** all OAM traffic. Games mutate the WRAM shadow freely at any time; `Video.EndFrame()` waits for vblank and fires OAM DMA once. OAM is the layer where immediate writes are least safe (inaccessible in modes 2 *and* 3), so it is never written directly.
- **Explicit bulk:** `TileSet.Load` and `Canvas.Present()` own their chunking: LCD off → straight `Mem.Copy`/GDMA; LCD on → vblank-budgeted drip (CGB: `Cgb.CopyToVram` in ≤2048-byte transfers; DMG: CPU copy in chunks derived from the measured `1528 + 300n` dot cost model, lifted from `double-buffered/Surface.cs` as named constants with the derivation in comments).

**Allocation discipline (no GC, no per-object free):**
- Everything touched per frame is **static WRAM**: shadow OAM is `[KohAligned(256)] static readonly byte[] Shadow = new byte[160]` — wait, initializer allocation isn't the model; it's a `static byte[160]`-equivalent fixed buffer field (non-readonly static array ⇒ WRAM per `CilMethodLowerer.Statics.cs`). Sprite state lives *in* the shadow (no second copy); library state (frame counter, LCDC mirror, dirty flags, font base tile) is a handful of static bytes.
- **ROM data by convention:** all tile/font tables are `static readonly byte[]` with literal initializers ⇒ `AddressSpace.Rom` automatically. Doc comments on `TileSet.Load`/`Font` warn that dropping `readonly` silently moves the table into scarce WRAM.
- **The arena is used exactly once:** `Canvas.Init` allocates its pixel buffer via `Mem.Alloc` (16-byte aligned by over-alloc, as `Surface.cs` does today, until/unless `[KohAligned]` grows an arena variant). Documented hard rule: *call `Canvas.Init` before your own allocations; `Mem.Reset()` destroys the canvas.* Nothing else in the library ever calls `Mem.Alloc`, and nothing allocates per frame.
- Structs over classes throughout: `Sprite` is a 1-byte struct handle (index into the shadow), not a heap object.

**Math discipline:** the CIL frontend applies standard C# promotion (byte×byte computes in int32, narrows on store), so the old wrap-mod-256 hazard is gone — but the library stays cast-clean at width boundaries the way `Tilemap.cs` already is (`(ushort)(row * 32 + col)` before pointer indexing), and public APIs take `int` coordinates where negative values are meaningful (sprite positions), `byte` where the domain is genuinely 0–255 (tile indices, map cells).

---

## 3. API SURFACE

All types `public`, namespace `Koh.GameBoy`, files under `src/Koh.GameBoy/Graphics/`.

### Video.cs — display lifecycle and the frame loop
```csharp
public static class Video
{
    public static bool IsCgb;                    // cached Cgb.IsColor(), set by Init
    public static byte FrameCount;               // free-running, ticks in EndFrame (animation/rng seed)

    public static void Init();                   // Lcd.Off, clear both tilemaps + shadow OAM, hide all
                                                 // sprites, default palettes (DMG 0xE4 / CGB grayscale),
                                                 // detect CGB. Screen stays off.
    public static void Start();                  // LCD on: BG enabled, $8000 tiles, map per layer config
    public static void Stop();                   // vblank-safe LCD off (wraps Lcd.Off)

    public static void ShowSprites(SpriteSize size);   // LCDC bits 1+2
    public static void HideSprites();
    public static void ShowWindow(byte x, byte y);     // LCDC bit 5 + bit 6 (window = map $9C00);
                                                       // x,y in SCREEN pixels — library adds WX's +7
    public static void HideWindow();
    public static void Scroll(byte x, byte y);         // SCX/SCY (wraps Lcd.Scroll)

    public static void EndFrame();               // THE frame call: Ppu.WaitVBlank(), flush shadow OAM
                                                 // (OAM DMA), apply pending palette writes, FrameCount++
}

public enum SpriteSize : byte { Size8x8 = 0, Size8x16 = 1 }
```
Replaces the hand-ordered boot in `gb-2048-cs/Game.cs` and the `Ppu.WaitVBlank()`-before-render idiom with `Init → author → Start → loop { ... EndFrame(); }`. No callback-based game loop — a plain `while (true)` with one required call is more honest in a subset with no delegates-per-frame budget, and matches how every sample is already shaped.

### TileSet.cs — getting pixels into VRAM
```csharp
public static class TileSet
{
    // Copy count tiles (16 bytes each) of 2bpp data into VRAM at firstTile ($8000 addressing).
    // LCD off: one straight copy (GDMA on CGB). LCD on: vblank-chunked internally — returns when done.
    public static void Load(byte firstTile, byte[] data);
    public static void Load(byte firstTile, byte[] data, ushort startTile, byte count);

    // Expand 1bpp source (8 bytes/tile) to 2bpp: set bits → color 'ink' (0-3), clear bits → color 'paper'.
    public static void Load1bpp(byte firstTile, byte[] mono, byte ink, byte paper);

    public static void SetRow(byte tile, byte row, byte low, byte high);  // passthrough to TileData.SetRow
    public static void Clear(byte tile);                                  // passthrough to TileData.Clear
}
```
Kills the biggest per-sample boilerplate: 2048's tileset becomes one `static readonly byte[]` + one `Load` call; the font ships as `Load1bpp` data (halves its ROM size).

### Bg.cs / Win.cs — the two tile layers
```csharp
public static class Bg      // background layer, map $9800
{
    public static void SetTile(byte col, byte row, byte tile);                    // immediate-checked
    public static void Fill(byte col, byte row, byte w, byte h, byte tile);       // rect of one tile
    public static void DrawMap(byte col, byte row, byte w, byte h, byte[] tiles); // blit ROM rect, row-major
    public static void Clear(byte tile);

    // CGB only (silent no-op on DMG): per-tile attributes in VRAM bank 1. Handles VBK internally.
    public static void SetAttr(byte col, byte row, byte attr);
    public static void FillAttr(byte col, byte row, byte w, byte h, byte attr);
}

public static class Win     // window layer, map $9C00 — identical surface minus Clear
{
    public static void SetTile(byte col, byte row, byte tile);
    public static void Fill(byte col, byte row, byte w, byte h, byte tile);
    public static void DrawMap(byte col, byte row, byte w, byte h, byte[] tiles);
    public static void SetAttr(byte col, byte row, byte attr);
}

public static class TileAttr    // composes the CGB attribute byte
{
    public const byte FlipX = 0x20, FlipY = 0x40, Priority = 0x80;
    public static byte Palette(byte n);           // n & 7
    // usage: Bg.SetAttr(c, r, (byte)(TileAttr.Palette(2) | TileAttr.FlipX))
}
```
Both delegate to one internal `MapWriter(byte* mapBase, ...)`. `Fill` is exactly the missing "paint an N×M block" from `RenderBoard`; `SetAttr` is the first way any Koh C# game can color/flip individual BG tiles on CGB.

### Font.cs / Text.cs
```csharp
public static class Font
{
    public static void LoadDefault(byte firstTile);  // built-in 96-glyph ASCII (0x20..0x7F) ROM font,
                                                     // 1bpp-expanded; remembers firstTile for Text
}

public static class Text
{
    public static void Draw(byte col, byte row, string text);          // to Bg
    public static void DrawToWindow(byte col, byte row, string text);  // to Win
    public static void DrawNumber(byte col, byte row, ushort value);            // decimal, left-aligned
    public static void DrawNumber(byte col, byte row, ushort value, byte width); // right-aligned, space-padded
}
```
Glyph = `firstTile + (ch - 0x20)`. Depends on the CIL frontend lowering string literals to ROM byte data — **verify-first item** (see Build Plan / Open Questions); `Draw(byte col, byte row, byte[] ascii)` is the guaranteed-expressible fallback overload either way.

### Sprites.cs — shadow OAM
```csharp
public struct Sprite                       // 1-byte handle into the shadow; copy-by-value is fine
{
    public byte Index;                     // 0..39

    public void Set(int x, int y, byte tile);       // screen coords; library adds the hardware +8/+16.
                                                    // Negative x/y clip off the left/top edge naturally
                                                    // ((byte)(x+8) wraps to the right hw value).
    public void Move(int x, int y);
    public void SetTile(byte tile);
    public void SetAttr(byte attr);                 // ObjAttr flags
    public void Hide();                             // Y=0 (fully off-screen)
}

public static class Sprites
{
    public static Sprite Get(byte index);           // no allocation/lifetime — a fixed pool of 40 slots;
    public static void HideAll();                   // slot ownership is the game's business (by design)
    // No Flush() in the public surface: Video.EndFrame() flushes. Sprites.Flush() exists internal
    // for tests and for games that manage vblank themselves via Ppu directly.
}

public static class ObjAttr
{
    public const byte Priority = 0x80, FlipY = 0x40, FlipX = 0x20, DmgPalette1 = 0x10;
    public static byte CgbPalette(byte n);          // n & 7
}
```
Backing store: `[KohAligned(256)]` static 160-byte WRAM shadow + a dirty flag. `EndFrame` fires `Hardware.RunOamDma()` when dirty (≈160 M-cycles, well inside vblank). Until the intrinsic lands (Build Plan slice 2), the fallback flush is a dirty-*range* CPU copy during vblank — honest for the handful-of-sprites case (`Mem.Copy` at ~300 dots/byte means a full 160-byte CPU copy is ~10× over the vblank budget, which is precisely why DMA is the target model, not an optimization).

### Palettes.cs / Rgb.cs — one color API for both machines
```csharp
public static class Rgb
{
    public static ushort Make(byte r, byte g, byte b);   // 0-31 each → RGB555
    public const ushort White = 0x7FFF, Black = 0x0000, /* Red, Green, Blue, ... a small named set */;
}

public static class Palettes
{
    // One call, both machines. CGB: writes palette RAM (BCPS/BCPD, OCPS/OCPD; immediate-checked).
    // DMG: slot 0 only — each color quantized to a 2-bit shade by luminance and composed into
    // BGP / OBP0 / OBP1 (OBJ slot 0 → OBP0, OBJ slot 1 → OBP1); slots 2-7 are no-ops on DMG.
    public static void SetBg(byte slot, ushort c0, ushort c1, ushort c2, ushort c3);
    public static void SetObj(byte slot, ushort c0, ushort c1, ushort c2, ushort c3); // c0 = transparent

    // Escape hatch: explicit DMG tuning when auto-quantization looks wrong. Overrides until the
    // next SetBg/SetObj on a DMG-visible slot.
    public static void SetDmgShades(byte bgp, byte obp0, byte obp1);
}
```
Decision (recon flagged this as unvalidated): **author once in RGB555, auto-degrade on DMG** — the default must let a CGB-authored game boot correctly on DMG with zero extra code, and `SetDmgShades` covers art-directed DMG looks. This replaces raw `Lcd.SetPalette(0xE4)` literals and the 4×`Cgb.SetBackgroundColor` boot dance in gb-3d, and delivers the first-ever OBJ coloring on both variants.

### Canvas.cs — the demo surface (consolidates the three `Surface.cs` + `SpanFill.cs`)
```csharp
public static class Canvas
{
    public static void Init(byte widthTiles, byte heightTiles, CanvasMode mode);
        // Allocates+aligns the pixel buffer from the Mem arena (call before your own Mem.Alloc;
        // Mem.Reset() kills the canvas). Zeroes VRAM tiles, lays out the tile grid centered on Bg,
        // reserves LCDC.4 for itself in DoubleBuffered mode (BlankTile=255 alias trick internal).

    public static int Width;  public static int Height;   // pixels

    public static void Clear(byte color);                          // color 0-3
    public static void SetPixel(int x, int y, byte color);         // color 0-3; no bounds check (demo-grade, documented)
    public static void FillSpan(int y, int x0, int x1, byte shade); // shade 0-7: even = solid 0-3,
                                                                    // odd = ordered 2x2 dither between neighbors
    public static void FillRect(int x, int y, int w, int h, byte shade);
    public static void DrawLine(int x0, int y0, int x1, int y1, byte color);
    public static void FillTriangle(int x0,int y0,int x1,int y1,int x2,int y2, byte shade);

    public static void Present();
        // CGB: GDMA via Cgb.CopyToVram, auto-split at the 2048-byte ceiling across vblanks.
        // DMG single-buffered: chunked vblank CPU copy (chunk size from the 1528+300n model, a named
        //   documented constant). DMG double-buffered: chunked copy to the back page, then LCDC.4 flip.
        // Buffers > one-vblank DMG budget: documented multi-frame present (the full-frame tradeoff),
        //   never a silent Lcd.Off flash — that stays an explicit sample technique, not library behavior.
}

public enum CanvasMode : byte { SingleBuffered, DoubleBuffered }   // DoubleBuffered: ≤ ~120 tiles/page
```
One canvas per program, static — justified by hardware: VRAM holds one such surface, and double-buffering consumes both tile-data pages. `SetPixel/FillSpan` bodies come verbatim from `racing-beam/Surface.cs` + `SpanFill.cs`; `DrawLine/FillTriangle` are lifted from `CubeRenderer` (they're generic rasterization, not app logic — `EdgeX` interpolation included).

### Joypad additions (in existing `Hal/Joypad.cs`, purely additive)
```csharp
public enum Button : byte { Right=0x01, Left=0x02, Up=0x04, Down=0x08, A=0x10, B=0x20, Select=0x40, Start=0x80 }

public static byte ReadAll();                       // full 8-button active-high mask (A/B/Select at last)
public static byte Pressed();                       // rising edges since previous Pressed() call
public static bool IsPressed(byte pressed, Button b);
```
Existing `Read()`/`Held()` and their 5-bit layout stay untouched (gb-2048-cs keeps compiling).

---

## 4. USAGE EXAMPLES

### (a) A 2D game — 2048 board + score + sprite cursor
```csharp
using Koh.GameBoy;

static class Game
{
    static readonly byte[] BoardTiles = { /* 12 tiles × 16 bytes, 2bpp */ };

    static void Main()
    {
        Video.Init();                                   // LCD off, everything cleared
        TileSet.Load(0, BoardTiles);
        Font.LoadDefault(0x80);
        Palettes.SetBg(0, Rgb.White, Rgb.Make(20,25,20), Rgb.Make(8,14,8), Rgb.Black);
        Palettes.SetObj(0, 0, Rgb.White, Rgb.Make(31,0,0), Rgb.Black);
        Text.Draw(1, 0, "SCORE");
        Video.ShowSprites(SpriteSize.Size8x8);
        Video.Start();

        Sprite cursor = Sprites.Get(0);
        cursor.Set(16, 32, 11);

        while (true)
        {
            byte pressed = Joypad.Pressed();            // rising edges, no hand-rolled prev mask
            if (Joypad.IsPressed(pressed, Button.Right)) { Board.Move(Direction.Right); RenderBoard(); }
            // ... other directions ...
            cursor.Move(16 + Board.CursorCol * 16, 32 + Board.CursorRow * 16);
            Text.DrawNumber(7, 0, Board.Score, 5);
            Video.EndFrame();                           // vblank + OAM flush, one call
        }
    }

    static void RenderBoard()
    {
        for (byte r = 0; r < 4; r++)
            for (byte c = 0; c < 4; c++)
                Bg.Fill((byte)(2 + c * 2), (byte)(4 + r * 2), 2, 2, Board.TileFor(r, c));
    }
}
```
Everything `Game.cs`/`Tiles.cs` hand-rolled — boot ordering, edge detection, `WaitVBlank` placement, 2×2 block loops — is gone, plus score text and a sprite that were previously impossible.

### (b) A demo — software-rendered spinning cube (gb-3d, double-buffered)
```csharp
static void Main()
{
    Video.Init();
    Palettes.SetBg(0, Rgb.White, Rgb.Make(21,21,21), Rgb.Make(10,10,10), Rgb.Black);
    Canvas.Init(8, 8, CanvasMode.DoubleBuffered);       // replaces ~100 lines of Surface.cs
    Video.Start();

    byte angle = 0;
    while (true)
    {
        Canvas.Clear(0);
        CubeRenderer.Render(angle);                     // transforms + sort + cull stay app code, but call
                                                        // Canvas.FillTriangle / Canvas.DrawLine
        Canvas.Present();                               // CGB GDMA or DMG chunked copy + page flip — hidden
        angle += 2;
    }
}
```
The entire per-variant `Surface.cs` and shared `SpanFill.cs` disappear; only the 3D math remains sample code.

---

## 5. DEMO

Validation is three-pronged — two sample retrofits (proving the API against *real* prior code) plus one new showcase sample with an emulator test:

1. **Extend `samples/gb-2048-cs`**: render the score with `Text.DrawNumber`, add a sprite cursor via `Sprites`, replace the boot/loop/tileset ceremony with `Video`/`TileSet`/`Bg.Fill`. This validates Text against a real need (recon B: designing a font API in a vacuum is a named risk) and Sprites against a real game loop.
2. **Port `samples/gb-3d/double-buffered`** to `Canvas`, deleting its `Surface.cs`, and re-run the existing `samples/gb-3d/verify` harness (Mode3WriteGuard / PhaseSweepCheck) — the tuned present budget must not regress; this is the recon-flagged regression trap, and the existing harness is exactly the tool to catch it.
3. **New `samples/gb-gfx-demo`** exercising every module in one ROM: scrolling checkerboard background (TileSet + Bg + Video.Scroll), a window HUD showing `SCORE 01234` (Win + Font/Text), four sprites orbiting the center with flip attributes (Sprites + ObjAttr), and a palette fade every 60 frames (Palettes, exercising both the CGB RGB path and the DMG quantized path).

**Emulator test** (TUnit, in `tests/Koh.GameBoy.Tests` or beside `CSharpEndToEndTests`, using the `Game2048Tests` harness: compile → link → `GameBoySystem` → step frames → inspect memory):
- After boot + 1 `EndFrame`: OAM bytes at `0xFE00` match the four sprites' expected Y/X/tile/attr (proving shadow flush + coordinate offsets); LCDC has BG|OBJ|WIN bits as configured.
- Tilemap cells at $9C00 spell `SCORE` as `fontBase + (ch-0x20)` indices; VRAM at the font base contains the expected glyph rows (proving `Load1bpp` expansion).
- BGP/OBP0 hold the quantized DMG shades for the palette set in code (DMG run); BCPD readback matches RGB555 (CGB run).
- After N frames with a held-direction joypad script: sprite OAM Y/X changed as scripted (proving `Pressed` + `Move` + per-frame flush).
- Canvas port: frame buffer VRAM bytes equal the reference desktop render of the same frame (the reference build shares the code path), and zero VRAM writes land in mode 3.

---

## 6. BUILD PLAN

Slices in dependency order; ★ = parallelizable with siblings once its arrows are satisfied.

1. **Hardware plumbing** — add `DMA`, `OBP0`, `OBP1`, `OCPS`, `OCPD`, `LYC` registers to `Hardware.cs` with desktop-host behavior ($FF46 write copies page→OAM on the reference build; OBP/OCP are plain cells), preserving existing Host render/pacing untouched. *Accept:* e2e ROM writes/reads each register on the emulator; desktop test confirms $FF46 side effect.
2. **Compiler pair** ★(parallel with 3–6) — `[KohAligned(n)]` on statics (WRAM allocator + desktop over-alloc) and the `oamdma` intrinsic (backend-emitted HRAM trampoline, boot-time install like the recursion stack relocation). *Accept:* e2e test fills a page-aligned WRAM buffer, calls `RunOamDma()`, asserts 160 OAM bytes; alignment test asserts the static's linked address is a multiple of 256; `IrVerifier` clean.
3. **Palettes + Rgb** ★ (needs 1) — includes the luminance→shade quantizer. *Accept:* emulator asserts BGP/OBP composition on DMG path and BCPD/OCPD contents on CGB path for the same source code.
4. **Video + Joypad additions** ★ (needs 1) — lifecycle, layer toggles, `EndFrame` skeleton (vblank + hooks for sprite/palette flush). *Accept:* LCDC bit assertions per config; `FrameCount` advances; after `EndFrame` returns, LY ∈ [144,153]; `Pressed()` edge test with scripted joypad.
5. **TileSet** ★ (needs 4) — LCD-off fast path + LCD-on chunked path + `Load1bpp`. *Accept:* VRAM byte equality for both paths; a Mode3WriteGuard-style check that the live path never writes during mode 3.
6. **Bg / Win / TileAttr** ★ (needs 4) — shared `MapWriter`, CGB attributes via VBK. *Accept:* map-byte assertions for `Fill`/`DrawMap` on both maps; VRAM-bank-1 attribute assertions on a CGB-mode run; DMG run asserts `SetAttr` is a true no-op.
7. **Font + Text** (needs 5, 6) — first, a spike verifying CIL string-literal lowering; fall back to `byte[]` overloads if it isn't there yet (Open Question 4). *Accept:* `SCORE`/`DrawNumber` map-index assertions; glyph pixel assertions.
8. **Sprites** (needs 1, 2, 4) — shadow, handles, EndFrame flush; dirty-range CPU fallback first if slice 2 lags, DMA when it lands. *Accept:* mutate all 40 slots, `EndFrame`, assert OAM == shadow; negative-coordinate clipping assertions; hidden sprite Y==0.
9. **Canvas** ★ (needs 4; parallel with 7–8) — lift `Surface.cs`×3 + `SpanFill.cs` + `CubeRenderer` raster prims into one type. *Accept:* the gb-3d double-buffered port renders pixel-identical frames vs the pre-port sample on the desktop reference build, and passes the gb-3d verify harness on the emulator.
10. **Demo + retrofits** (needs all) — `gb-gfx-demo`, the 2048 and gb-3d retrofits, and the §5 e2e test suite. *Accept:* the §5 assertions, plus `dotnet build Koh.Ci.slnf` with zero warnings and `dotnet msbuild build.proj -t:Test` green.

---

## 7. OPEN QUESTIONS

1. **Canvas in v1 — confirm or cut.** This design says v1 (it's consolidation of thrice-duplicated, hardware-verified sample code, and demos are half the audience). Cutting it makes v1 ~30% smaller but leaves gb-3d untouched by the library. If cut, `FillSpan`'s dither math should still land somewhere shared, or the 3× duplication persists.
2. **DMG degradation policy for palettes.** Chosen: author RGB555 once, auto-quantize to DMG shades by luminance, `SetDmgShades` as the override. Alternative: require explicit dual authoring (`SetBg(slot, colors..., dmgShades)`) — more art control, more ceremony for the common case. This affects every game's boot code; worth a human call since no existing sample exercises both machines at once.
3. **Naming/namespace.** `Video` vs `Display` for the lifecycle class (`Display` reads nicer, `Video` avoids colliding with a plausible future desktop-side `Display` type); and flat `namespace Koh.GameBoy` (chosen, matches Hal) vs a `Koh.GameBoy.Graphics` sub-namespace that keeps the high-level surface visually separate.
4. **String literals in the CIL frontend.** `Text.Draw(..., string)` assumes `ldstr` lowers to ROM byte data. The frontend is in-flight (uncommitted `Frontends/Cil/`); if strings aren't supported at implementation time, does Text ship `byte[]`-only for v1, or is string-literal lowering added to the frontend as part of this work? (Recommendation: add it — `Text.Draw(1, 0, "SCORE")` vs a byte-array literal is exactly the "pleasant API" the goal asks for.)

Key file paths referenced: `src/Koh.GameBoy/Hal/{Lcd,Ppu,Tilemap,TileData,Cgb,Joypad}.cs`, `src/Koh.GameBoy/{Hardware,Gb,Mem}.cs`, `samples/gb-2048-cs/{Game,Tiles,Board}.cs`, `samples/gb-3d/{shared/SpanFill.cs,shared/CubeRenderer.cs,*/Surface.cs}`, `src/Koh.Compiler/Frontends/Cil/CilMethodLowerer.Statics.cs`, `docs/superpowers/specs/2026-07-14-cil-frontend-design.md`.
---

## 8. RESOLVED DECISIONS (authoritative — override §7)

Reviewed with the maintainer; these override the Open Questions above and the affected API sketches in §3.

1. **Canvas IS in v1.** Build it, port gb-3d/double-buffered to it, run the gb-3d verify harness (present budget must not regress).
2. **Palettes use EXPLICIT DUAL AUTHORING** — no luminance auto-quantization. Every palette call takes the CGB colors AND the DMG shade byte:
   - `Palettes.SetBg(byte slot, ushort c0, ushort c1, ushort c2, ushort c3, byte dmgShades)`
   - `Palettes.SetObj(byte slot, ushort c0, ushort c1, ushort c2, ushort c3, byte dmgShades)` (c0 transparent)
   On CGB the RGB555 colors are written to palette RAM; on DMG the `dmgShades` byte is written to BGP/OBP0/OBP1 (slot picks which OBP). Drop the auto-quantizer and the separate `SetDmgShades` escape hatch — dual authoring makes it redundant.
3. **`Text.Draw(..., string)` ships with real string literals.** Add `ldstr`→ROM-byte lowering to the CIL frontend (ASCII bytes into an `AddressSpace.Rom` global, mirroring the existing u8/array-literal ROM-data path) as build-plan **slice 0**, before Font/Text. Keep a `byte[]` overload too, but the string overload must work.
4. **Namespace `Koh.GameBoy.Graphics`; lifecycle class `Video`.** All new Graphics types live in `namespace Koh.GameBoy.Graphics` under `src/Koh.GameBoy/Graphics/`. A game adds `using Koh.GameBoy.Graphics;` in addition to `using Koh.GameBoy;`. The Hal layer stays in `Koh.GameBoy`.

Revised build-plan order: **slice 0 (ldstr lowering, CIL frontend)** → slice 1 (Hardware plumbing) → slice 2 (compiler pair: `[KohAligned]` + `oamdma`) → slices 3–9 (modules) → slice 10 (demo + retrofits). Slice 0 is compiler work (verify with an assembly→ROM→GameBoySystem test reading the ROM'd string bytes), independent of the Hardware plumbing.
