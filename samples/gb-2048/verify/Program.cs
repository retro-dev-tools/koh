// Gb2048Verify — drives the gb-2048 ROM through every game state, captures
// snapshots, and emits two composite views so the design can be reviewed
// at a glance:
//   * shot_contact.png — every game state in one annotated PNG.
//   * shot_anim.gif    — the 12-frame slide animation as a real GIF.
//
// Per-state PNGs go to <out>/shot_*.png and the contact sheet + gif go to
// <out>/shot_contact.png and <out>/shot_anim.gif.
//
// Run: dotnet run --project samples/gb-2048/verify -- [<rom>] [<out>]
using Koh.Emulator.Core.Joypad;
using Koh.Verify;

string rom = args.Length > 0
    ? args[0]
    : Path.Combine("samples", "gb-2048", "build", "2048.gbc");
string outDir = args.Length > 1
    ? args[1]
    : Path.Combine(Path.GetDirectoryName(rom)!, "verify-shots");
Directory.CreateDirectory(outDir);

var h = new RomHarness(rom);
var sheet = new List<ContactSheet.Tile>();
var animFrames = new List<byte[]>();
string Shot(string n) => Path.Combine(outDir, $"shot_{n}.png");
void Capture(string name, string label)
{
    var rgb = h.CaptureRgb();
    sheet.Add(new ContactSheet.Tile(label, rgb));
    h.SaveScreenshotPng(Shot(name));
    Console.WriteLine($"  captured '{label}' -> {name}");
}

// ==================  Boot + Title  =================================
h.Frames(120);
Console.WriteLine("=== Title ===");
Capture("title", "TITLE");
h.Assert(h.Read(0xC06E) == 0x00, "In TITLE state after boot");
h.Assert(h.TileAt(0, 0) == 2, "HUD 'BEST' label present");
h.Assert(h.TileAt(2, 10) == 39, "HUD divider rule on row 2");

// ==================  Playing (fresh)  ==============================
h.Press(JoypadButton.Start);
h.Frames(30);
Console.WriteLine("=== Playing (fresh) ===");
Capture("playing", "PLAYING - 2 STARTING TILES");
h.Assert(h.Read(0xC06E) == 0x01, "In PLAYING after START");
int nonEmpty = 0;
for (int i = 0; i < 16; i++) if (h.Read((ushort)(0xC000 + i)) != 0) nonEmpty++;
h.Assert(nonEmpty == 2, $"Fresh board has 2 starting tiles (saw {nonEmpty})");

void VerifyBoardRender(string label)
{
    // Layout: cells start at row 4 col 2, 3 rows tall, 4 cols wide, no gaps.
    // Cell (r,c) top-left tile is at tilemap (4 + r*3, 2 + c*4).
    for (int cellRow = 0; cellRow < 4; cellRow++)
    for (int cellCol = 0; cellCol < 4; cellCol++)
    {
        int idx = cellRow * 4 + cellCol;
        byte val = h.Read((ushort)(0xC000 + idx));
        int topRow = 4 + cellRow * 3;
        int leftCol = 2 + cellCol * 4;
        if (h.TileAt(topRow, leftCol) != 31)
            h.Fail($"{label}: cell({cellRow},{cellCol}) top edge wrong (got {h.TileAt(topRow, leftCol)})");
        if (h.TileAt(topRow + 2, leftCol) != 32)
            h.Fail($"{label}: cell({cellRow},{cellCol}) bot edge wrong (got {h.TileAt(topRow + 2, leftCol)})");
        byte attr = h.AttrAt(topRow + 1, leftCol);
        byte expected = val switch
        {
            0 => 1, 1 => 2, 2 => 3, 3 => 4, 4 => 4, 5 => 5, 6 => 5,
            7 => 6, 8 => 6, 9 => 6, _ => 7
        };
        if ((attr & 7) != expected)
            h.Fail($"{label}: cell({cellRow},{cellCol}) val={val} palette={attr & 7}, expected {expected}");
    }
}
VerifyBoardRender("fresh");

// ==================  Play 10 moves; verify renderer  ===============
Console.WriteLine("=== Multi-move sequence ===");
var dirs = new[] { JoypadButton.Left, JoypadButton.Down, JoypadButton.Right, JoypadButton.Up };
// Per-frame trace of regular move 1 (to compare against win trace).
{
    var jp = default(Koh.Emulator.Core.Joypad.JoypadState);
    jp.Press(dirs[0]);
    h.System.Joypad = jp;
    for (int f = 1; f <= 12; f++)
    {
        h.Frames(1);
        Console.Write($"  M1+{f}f af=${h.Read(0xC070):x2} ws=${h.Read(0xC063):x2}{h.Read(0xC062):x2}{h.Read(0xC061):x2}{h.Read(0xC060):x2} digits=[");
        for (int i = 7; i < 14; i++) Console.Write($"{h.TileAt(1, i):x2} ");
        Console.WriteLine("]");
    }
    h.System.Joypad = default;
    h.Frames(20);
}
Console.WriteLine("After move 1, BG tilemap rows 3..15:");
for (int row = 3; row <= 15; row++)
{
    Console.Write($"  r{row:00}: ");
    for (int col = 0; col < 20; col++) Console.Write($"{h.TileAt(row, col):x2} ");
    Console.Write("attr=[");
    for (int col = 0; col < 20; col++) Console.Write($"{h.AttrAt(row, col) & 7} ");
    Console.WriteLine("]");
}
VerifyBoardRender("after move 1");
Capture("after_move1", "AFTER MOVE 1");
for (int m = 1; m < 10; m++)
{
    h.Press(dirs[m % 4]);
    h.Frames(20);
    VerifyBoardRender($"after move {m + 1}");
}
Capture("after_moves", "AFTER 10 MOVES");

// ==================  Slide-animation capture (GIF + sheet)  ========
for (int i = 0; i < 16; i++) h.Write((ushort)(0xC000 + i), 0);
h.Write(0xC00C, 1); h.Write(0xC00F, 1);
// Snapshot the pre-press state too.
animFrames.Add(h.CaptureRgb());
{
    var jp = default(JoypadState);
    jp.Press(JoypadButton.Right);
    h.System.Joypad = jp;
    h.Frames(2);
    h.System.Joypad = default;
}
for (int f = 1; f <= 12; f++)
{
    h.Frames(1);
    animFrames.Add(h.CaptureRgb());
}
GifEncoder.WriteAnimation(Path.Combine(outDir, "shot_anim.gif"), animFrames, 160, 144,
                           frameDelayCs: 6, loopCount: 0);
Console.WriteLine($"  wrote {Path.Combine(outDir, "shot_anim.gif")} ({animFrames.Count} frames)");

// Strip view of the whole animation so we can eyeball the flash / smoothness.
var animTiles = new List<ContactSheet.Tile>();
for (int i = 0; i < animFrames.Count; i++)
    animTiles.Add(new ContactSheet.Tile($"F{i}", animFrames[i]));
ContactSheet.WriteStrip(Path.Combine(outDir, "shot_anim_strip.png"), animTiles, scale: 2);
Console.WriteLine($"  wrote {Path.Combine(outDir, "shot_anim_strip.png")}");

sheet.Add(new ContactSheet.Tile("MID-SLIDE F4", animFrames[4]));
sheet.Add(new ContactSheet.Tile("POST-COMMIT F9", animFrames[9]));
h.Assert(h.Read(0xC00F) == 2, $"Right-merge produced value 2 at (3,3), got {h.Read(0xC00F)}");

// Per-direction slide strips. The Right strip above only exercises the
// X-positive sprite path; Down/Up cover the vertical (stride-24, *3) math and
// Left covers X-negative. Each merges two equal tiles so we can eyeball that
// the moving sprites stay inside the grid cells for every direction.
//   idxA/idxB: cells to seed; dir: button; mergeIdx/mergeVal: post-commit check.
void CaptureSlideStrip(string name, JoypadButton dir, int idxA, int idxB,
                       int mergeIdx, byte mergeVal)
{
    for (int i = 0; i < 16; i++) h.Write((ushort)(0xC000 + i), 0);
    h.Write((ushort)(0xC000 + idxA), 1);
    h.Write((ushort)(0xC000 + idxB), 1);
    h.Write(0xC06E, 0x01);                 // ensure PLAYING so the move triggers
    var frames = new List<byte[]> { h.CaptureRgb() };
    var jp = default(JoypadState);
    jp.Press(dir);
    h.System.Joypad = jp;
    h.Frames(2);
    h.System.Joypad = default;
    for (int f = 1; f <= 12; f++) { h.Frames(1); frames.Add(h.CaptureRgb()); }
    var tiles = new List<ContactSheet.Tile>();
    for (int i = 0; i < frames.Count; i++)
        tiles.Add(new ContactSheet.Tile($"F{i}", frames[i]));
    ContactSheet.WriteStrip(Path.Combine(outDir, $"shot_anim_{name}_strip.png"), tiles, scale: 2);
    sheet.Add(new ContactSheet.Tile($"{name.ToUpper()} F4", frames[4]));
    Console.WriteLine($"  wrote shot_anim_{name}_strip.png");
    h.Frames(20);
    h.Assert(h.Read((ushort)(0xC000 + mergeIdx)) == mergeVal,
        $"{name}-merge produced value {mergeVal} at cell {mergeIdx}, got {h.Read((ushort)(0xC000 + mergeIdx))}");
}
// Down: col 0, rows 0 & 3 -> merge at row 3 (idx 12). d_row>0 (Y-positive).
CaptureSlideStrip("down", JoypadButton.Down, 0, 12, 12, 2);
// Up: same column -> merge at row 0 (idx 0). d_row<0 (Y-negative).
CaptureSlideStrip("up", JoypadButton.Up, 0, 12, 0, 2);
// Left: row 3, cols 0 & 3 -> merge at col 0 (idx 12). d_col<0 (X-negative).
CaptureSlideStrip("left", JoypadButton.Left, 12, 15, 12, 2);

// ==================  Game over  =====================================
byte[] dead = {
    1, 2, 1, 2,
    2, 3, 2, 1,
    1, 2, 1, 3,
    0, 2, 1, 3,
};
for (int i = 0; i < 16; i++) h.Write((ushort)(0xC000 + i), dead[i]);
h.Write(0xC06E, 0x01);
h.Press(JoypadButton.Left);
h.Frames(60);
Console.WriteLine("=== Game over ===");
Capture("gameover", "GAME OVER");
h.Assert(h.Read(0xC06E) == 0x04, "Triggered GS_GAMEOVER");

// ==================  Win  ===========================================
h.Press(JoypadButton.Start);
h.Frames(30);
h.Press(JoypadButton.Start);
h.Frames(30);
h.Write(0xC06F, 0);
for (int i = 0; i < 16; i++) h.Write((ushort)(0xC000 + i), 0);
h.Write(0xC00C, 10);
h.Write(0xC00D, 10);
// Press LEFT manually and trace per frame so we can see when HUD updates.
{
    var jp = default(Koh.Emulator.Core.Joypad.JoypadState);
    jp.Press(JoypadButton.Left);
    h.System.Joypad = jp;
    for (int f = 1; f <= 14; f++)
    {
        h.Frames(1);
        Console.Write($"  +{f}f af=${h.Read(0xC070):x2} gs=${h.Read(0xC06E):x2} ");
        Console.Write("r0=[");
        for (int i = 0; i < 14; i++) Console.Write($"{h.TileAt(0, i):x2} ");
        Console.Write("] r1=[");
        for (int i = 0; i < 14; i++) Console.Write($"{h.TileAt(1, i):x2} ");
        Console.WriteLine("]");
    }
    h.System.Joypad = default;
    h.Frames(20);
}
Console.WriteLine("=== Win ===");
Capture("win", "YOU WIN");
h.Assert(h.Read(0xC06E) == 0x03, "Triggered GS_WIN");

// ==================  Contact sheet  =================================
ContactSheet.Write(Path.Combine(outDir, "shot_contact.png"), sheet, columns: 3, scale: 2);
Console.WriteLine($"  wrote {Path.Combine(outDir, "shot_contact.png")} ({sheet.Count} tiles)");

h.Summary();
return h.ExitCode;
