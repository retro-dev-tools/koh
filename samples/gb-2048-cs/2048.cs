// 2048 for the Game Boy, written in Koh C#.
//
// This is a complete, bootable ROM compiled by the Koh compiler platform:
//   Koh C# frontend  ->  typed SSA IR  ->  hand-written SM83 backend  ->  .gb
//
// It sticks to the supported "Koh C#" systems subset: byte / sbyte / ushort /
// bool, raw pointers (byte*), local arrays, and the built-in Hardware surface
// for memory-mapped I/O. There is no `int`, no heap, no garbage collector - the
// whole game runs out of a single 16-byte board that lives in the Main frame.
//
// Tiles are stored as *exponents*: 0 = empty, 1 = "2", 2 = "4", ... 11 = "2048".
// Merging two equal tiles is therefore just `exponent + 1`, which keeps every
// value in a byte and makes the slide/merge logic trivial to follow.
//
// Controls:  D-pad  = slide the board      Start = (reserved)
//
// Graphics are intentionally minimal: each tile value is drawn as a solid
// framed block in one of the four DMG shades (procedurally generated at boot,
// since ROM data arrays are not yet part of the Koh C# subset).

// ---- Board geometry -------------------------------------------------------

// One board is 4x4 exponents laid out row-major in 16 bytes. `board` is always
// a pointer to cell (0,0); cell (r,c) is at board[r*4 + c].

// Slide+merge four contiguous cells toward index 0 (the classic 2048 line move).
// Runs the canonical three-step algorithm: compact, merge equal neighbours,
// compact again. Merging zeroes the absorbed cell, so a run like 2 2 2 2 folds
// into two 4s (never a single 8), exactly like the real game.
// Pull every non-empty cell down toward index 0, leaving the freed high cells zero.
static void Compact(byte* a)
{
    byte write = 0;
    for (byte read = 0; read < 4; read++)
    {
        if (*(a + read) != 0)
        {
            byte v = *(a + read);
            *(a + read) = 0;
            *(a + write) = v;
            write++;
        }
    }
}

static void SlideLine(byte* a)
{
    // 1. Compact so the cells are packed against index 0.
    Compact(a);

    // 2. Merge: fold each equal adjacent pair once (left-biased). Zeroing the
    //    right cell means the next iteration skips it, so triples merge once.
    for (byte i = 0; i < 3; i++)
    {
        if (*(a + i) != 0 && *(a + i) == *(a + i + 1))
        {
            *(a + i) = (byte)(*(a + i) + 1);
            *(a + i + 1) = 0;
        }
    }

    // 3. Compact again to close the gaps the merge opened.
    Compact(a);
}

// Map a line position to a board index for one of the four move directions.
// `k` selects the row (Left/Right) or column (Up/Down); `j` is the position
// counting outward from the edge the tiles slide toward (0 = against the wall).
//   dir 0 = Left, 1 = Right, 2 = Up, 3 = Down.
static byte SrcIndex(byte dir, byte k, byte j)
{
    if (dir == 0) return (byte)(k * 4 + j);           // row k, toward col 0
    if (dir == 1) return (byte)(k * 4 + (3 - j));     // row k, toward col 3
    if (dir == 2) return (byte)(j * 4 + k);           // col k, toward row 0
    return (byte)((3 - j) * 4 + k);                   // col k, toward row 3
}

// Slide the whole board in one direction. Gathers each of the four lines into a
// temporary, runs SlideLine, and scatters it back. Returns 1 if any cell moved.
static byte MoveDir(byte* board, byte dir)
{
    byte changed = 0;
    for (byte k = 0; k < 4; k++)
    {
        byte[] line = new byte[4];
        for (byte j = 0; j < 4; j++)
            line[j] = *(board + SrcIndex(dir, k, j));

        SlideLine(&line[0]);

        for (byte j = 0; j < 4; j++)
        {
            byte idx = SrcIndex(dir, k, j);
            if (line[j] != *(board + idx))
                changed = 1;
            *(board + idx) = line[j];
        }
    }
    return changed;
}

// ---- Spawning & game state ------------------------------------------------

// Drop a new tile into a random empty cell. `rnd` supplies entropy (the caller
// feeds it from the DIV timer). New tiles are "2" (exponent 1) most of the time
// and "4" (exponent 2) occasionally. Returns 0 if the board was already full.
static byte SpawnTile(byte* board, byte rnd)
{
    byte count = 0;
    for (byte i = 0; i < 16; i++)
        if (*(board + i) == 0)
            count++;
    if (count == 0)
        return 0;

    byte pick = (byte)(rnd % count);
    byte val = ((rnd & 0x10) == 0) ? (byte)1 : (byte)2;

    for (byte i = 0; i < 16; i++)
    {
        if (*(board + i) == 0)
        {
            if (pick == 0)
            {
                *(board + i) = val;
                return 1;
            }
            pick--;
        }
    }
    return 1;
}

// A 2048 tile (exponent 11) exists somewhere on the board.
static byte HasWon(byte* board)
{
    for (byte i = 0; i < 16; i++)
        if (*(board + i) >= 11)
            return 1;
    return 0;
}

// Any legal move remains: an empty cell, or two equal neighbours in a row/column.
static byte CanMove(byte* board)
{
    for (byte i = 0; i < 16; i++)
        if (*(board + i) == 0)
            return 1;

    for (byte r = 0; r < 4; r++)
        for (byte c = 0; c < 3; c++)
            if (*(board + r * 4 + c) == *(board + r * 4 + c + 1))
                return 1;

    for (byte c = 0; c < 4; c++)
        for (byte r = 0; r < 3; r++)
            if (*(board + r * 4 + c) == *(board + (r + 1) * 4 + c))
                return 1;

    return 0;
}

// ---- Video ----------------------------------------------------------------

// Build 12 background tiles at $8000: tile 0 is empty (lightest shade); tiles
// 1..11 are framed blocks whose interior shade cycles so adjacent values read
// differently. Each tile is 8 rows x 2 bit-planes = 16 bytes.
static void GenTiles()
{
    byte* vram = (byte*)0x8000;

    for (byte i = 0; i < 16; i++)
        *(vram + i) = 0; // tile 0: all colour 0

    for (byte t = 1; t < 12; t++)
    {
        byte c = (byte)(1 + (t % 3));                       // interior shade 1..3
        byte fillLo = ((c & 1) != 0) ? (byte)0x7E : (byte)0x00; // interior bits 1..6
        byte fillHi = ((c & 2) != 0) ? (byte)0x7E : (byte)0x00;

        byte* p = vram + t * 16;
        for (byte row = 0; row < 8; row++)
        {
            byte lo;
            byte hi;
            if (row == 0 || row == 7)
            {
                lo = 0xFF; // top/bottom frame row: colour 3
                hi = 0xFF;
            }
            else
            {
                lo = (byte)(fillLo | 0x81); // interior + left/right frame pixels
                hi = (byte)(fillHi | 0x81);
            }
            *(p + row * 2) = lo;
            *(p + row * 2 + 1) = hi;
        }
    }
}

// Paint the board into the background tilemap at $9800. Each cell is drawn as a
// 2x2 block of its value's tile, spaced out into a grid.
static void Render(byte* board)
{
    byte* map = (byte*)0x9800;
    for (byte r = 0; r < 4; r++)
    {
        for (byte c = 0; c < 4; c++)
        {
            byte tile = *(board + r * 4 + c);
            byte baseRow = (byte)(3 + r * 3);
            byte baseCol = (byte)(3 + c * 4);
            for (byte dr = 0; dr < 2; dr++)
                for (byte dc = 0; dc < 2; dc++)
                {
                    // Force 16-bit math: a tilemap row is 32 tiles, so row*32 overflows a byte
                    // for the lower rows (8-bit arithmetic would truncate before the widening cast).
                    ushort row = (ushort)(baseRow + dr);
                    ushort off = (ushort)(row * 32 + baseCol + dc);
                    *(map + off) = tile;
                }
        }
    }
}

// ---- Input ----------------------------------------------------------------

// Read the joypad and return an active-high bitmask:
//   bit0 Right, bit1 Left, bit2 Up, bit3 Down, bit4 Start.
static byte ReadButtons()
{
    Hardware.JOYP = 0x20;          // select the d-pad (P14 low)
    byte d = Hardware.JOYP;
    d = Hardware.JOYP;             // read twice to let the lines settle

    Hardware.JOYP = 0x10;          // select the buttons (P15 low)
    byte b = Hardware.JOYP;
    b = Hardware.JOYP;

    Hardware.JOYP = 0x30;          // deselect

    // Inputs are active-low; ~x on a byte is (255 - x).
    byte dd = (byte)((byte)(255 - d) & 0x0F);
    byte bb = (byte)((byte)(255 - b) & 0x0F);
    byte start = ((bb & 0x08) != 0) ? (byte)0x10 : (byte)0x00; // buttons bit3 = Start
    return (byte)(dd | start);
}

// Spin until the LCD enters vertical blank, so tilemap writes don't tear.
static void WaitVBlank()
{
    while (Hardware.LY == 144) { }   // leave the current vblank, if in one
    while (Hardware.LY != 144) { }   // wait for the next one
}

// ---- Entry point ----------------------------------------------------------

static void Main()
{
    Hardware.LCDC = 0x00;   // LCD off so we can touch VRAM freely
    Hardware.BGP = 0xE4;    // palette: 11 10 01 00 (dark -> light)
    Hardware.SCY = 0x00;
    Hardware.SCX = 0x00;

    GenTiles();

    byte[] board = new byte[16];
    for (byte i = 0; i < 16; i++)
        board[i] = 0;

    SpawnTile(&board[0], Hardware.DIV);
    SpawnTile(&board[0], Hardware.DIV);
    Render(&board[0]);

    Hardware.LCDC = 0x91;   // LCD on, BG on, tile data $8000, tilemap $9800

    byte prev = 0;
    while (true)
    {
        byte btn = ReadButtons();
        byte pressed = (byte)((byte)(btn ^ prev) & btn); // rising edges only
        prev = btn;

        byte moved = 0;
        if ((pressed & 0x02) != 0) moved = MoveDir(&board[0], 0);       // Left
        else if ((pressed & 0x01) != 0) moved = MoveDir(&board[0], 1);  // Right
        else if ((pressed & 0x04) != 0) moved = MoveDir(&board[0], 2);  // Up
        else if ((pressed & 0x08) != 0) moved = MoveDir(&board[0], 3);  // Down

        if (moved != 0)
        {
            SpawnTile(&board[0], Hardware.DIV);
            WaitVBlank();
            Render(&board[0]);
        }
    }
}
