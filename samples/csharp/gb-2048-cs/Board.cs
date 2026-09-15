// The 2048 board: a 4x4 grid of tile *exponents* (0 = empty, 1 = "2", 2 = "4", ... 11 = "2048"),
// stored row-major in 16 bytes of work RAM. Merging two equal tiles is just `exponent + 1`, which
// keeps every value in a byte. All of the game's rules live here, behind a small typed API; nothing
// outside this file touches the raw cells. (`Direction` comes from the Koh.GameBoy framework.)

namespace Koh.Samples.Gb2048CSharp;

static unsafe class Board
{
    // The 16 cells live in a static WRAM buffer (zero = empty at boot).
    static byte[] cells = new byte[16];

    // ---- Access ----------------------------------------------------------

    // The grid is exactly 16 cells; masking the index keeps a stray caller in bounds so both targets
    // behave the same (the ROM has no bounds check and would otherwise scribble adjacent WRAM).
    internal static byte GetCell(byte index) => cells[index & 15];

    internal static void SetCell(byte index, byte value)
    {
        cells[index & 15] = value;
    }

    // The tile at (row, col), for the renderer.
    internal static byte Tile(byte row, byte col) => cells[(row * 4 + col) & 15];

    internal static void Reset()
    {
        for (byte i = 0; i < 16; i++)
            cells[i] = 0;
        Score = 0;
        CursorRow = 0;
        CursorCol = 0;
    }

    // ---- Score & cursor ----------------------------------------------------
    // Added for the graphics-library retrofit: neither had anywhere to live before there was a way to
    // render them (Text.DrawNumber / Sprites).

    // Classic 2048 scoring: each merge adds the value of the tile it produces (2^newExponent), tallied
    // where the only merge happens — SlideLine. Cleared by Reset().
    internal static ushort Score;

    // The board cell (row, col) SpawnTile most recently placed a tile in — the "active" cell Game.cs's
    // sprite cursor tracks. (0, 0) until the first spawn.
    internal static byte CursorRow;
    internal static byte CursorCol;

    // ---- Moves -----------------------------------------------------------

    // Pull every non-empty cell of a four-cell line down toward index 0.
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

    // The classic 2048 line move: compact, merge equal neighbours once (left-biased), compact again.
    // Zeroing the absorbed cell means 2 2 2 2 folds into two 4s, never a single 8.
    static void SlideLine(byte* a)
    {
        Compact(a);
        for (byte i = 0; i < 3; i++)
        {
            if (*(a + i) != 0 && *(a + i) == *(a + i + 1))
            {
                byte merged = (byte)(*(a + i) + 1);
                *(a + i) = merged;
                *(a + i + 1) = 0;
                Score += (ushort)(1 << merged);
            }
        }
        Compact(a);
    }

    // Map a line position to a board index for a move direction. `k` selects the row (Left/Right) or
    // column (Up/Down); `j` counts outward from the edge the tiles slide toward (0 = against the wall).
    static byte SrcIndex(Direction dir, byte k, byte j)
    {
        if (dir == Direction.Left)
            return (byte)(k * 4 + j);
        if (dir == Direction.Right)
            return (byte)(k * 4 + (3 - j));
        if (dir == Direction.Up)
            return (byte)(j * 4 + k);
        return (byte)((3 - j) * 4 + k); // Down
    }

    // Slide the whole board one direction: gather each line into a temp, slide it, scatter it back.
    // Returns true if any cell changed.
    internal static bool Slide(Direction dir)
    {
        byte* line = stackalloc byte[4]; // one scratch line, refilled for each of the four lines
        bool changed = false;
        for (byte k = 0; k < 4; k++)
        {
            for (byte j = 0; j < 4; j++)
                *(line + j) = cells[SrcIndex(dir, k, j)];

            SlideLine(line);

            for (byte j = 0; j < 4; j++)
            {
                byte idx = SrcIndex(dir, k, j);
                if (*(line + j) != cells[idx])
                    changed = true;
                cells[idx] = *(line + j);
            }
        }
        return changed;
    }

    // ---- State -----------------------------------------------------------

    // A new tile is "2" mostly, "4" occasionally, dropped into a random empty cell, using the DIV timer
    // as entropy. Returns false if the board was already full.
    internal static bool SpawnTile()
    {
        byte rnd = Hardware.DIV;

        byte count = 0;
        for (byte i = 0; i < 16; i++)
            if (cells[i] == 0)
                count++;
        if (count == 0)
            return false;

        byte pick = (byte)(rnd % count);
        byte val = ((rnd & 0x10) == 0) ? (byte)1 : (byte)2;

        for (byte i = 0; i < 16; i++)
        {
            if (cells[i] == 0)
            {
                if (pick == 0)
                {
                    cells[i] = val;
                    CursorRow = (byte)(i / 4);
                    CursorCol = (byte)(i % 4);
                    return true;
                }
                pick--;
            }
        }
        return true;
    }

    // A 2048 tile (exponent 11) is on the board.
    internal static bool HasWon()
    {
        for (byte i = 0; i < 16; i++)
            if (cells[i] >= 11)
                return true;
        return false;
    }

    // Any legal move remains: an empty cell, or two equal neighbours in a row or column.
    internal static bool CanMove()
    {
        for (byte i = 0; i < 16; i++)
            if (cells[i] == 0)
                return true;

        for (byte r = 0; r < 4; r++)
        for (byte c = 0; c < 3; c++)
            if (cells[r * 4 + c] == cells[r * 4 + c + 1])
                return true;

        for (byte c = 0; c < 4; c++)
        for (byte r = 0; r < 3; r++)
            if (cells[r * 4 + c] == cells[(r + 1) * 4 + c])
                return true;

        return false;
    }
}
