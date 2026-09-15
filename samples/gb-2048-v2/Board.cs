// Pure 2048 game logic — no hardware, no rendering, no unsafe. `Line` is a plain value struct that
// methods RETURN BY VALUE (Board.ReadLine): the natural C# shape, and one of the constructs this
// north-star sample exists to force (compiler enabler E1, sret lowering). `Board` is an ordinary
// class: readonly array field initialized inline, auto-property with private setter, an indexer —
// all standard C# the ideal API refuses to bend around backend limitations.
using Koh.GameBoy;
using Koh.GameBoy.Framework;

namespace Koh.Samples.Gb2048V2;

/// <summary>One row or column of the board, normalized so sliding always moves toward index 0.
/// Cell values are exponents: 0 is empty, 1 is "2", 11 is "2048".</summary>
struct Line
{
    private byte _a,
        _b,
        _c,
        _d;

    public byte this[int i]
    {
        get =>
            i switch
            {
                0 => _a,
                1 => _b,
                2 => _c,
                _ => _d,
            };
        set
        {
            if (i == 0)
                _a = value;
            else if (i == 1)
                _b = value;
            else if (i == 2)
                _c = value;
            else
                _d = value;
        }
    }

    /// <summary>Slide toward index 0 and merge equal neighbors, each tile merging at most once —
    /// standard 2048 semantics. Returns the points gained, or -1 if nothing moved at all
    /// (the caller uses "moved" to decide whether a new tile spawns).</summary>
    public int Compact()
    {
        bool moved = false;
        int gained = 0;

        // 1. Slide: pull every nonzero cell toward index 0, preserving order.
        int write = 0;
        for (int read = 0; read < 4; read++)
        {
            byte v = this[read];
            if (v == 0)
                continue;
            if (write != read)
            {
                this[write] = v;
                this[read] = 0;
                moved = true;
            }
            write++;
        }

        // 2. Merge: each equal adjacent pair collapses once, and the tail re-slides behind it.
        for (int i = 0; i < 3; i++)
        {
            if (this[i] == 0 || this[i] != this[i + 1])
                continue;
            this[i]++;
            gained += 1 << this[i];
            for (int j = i + 1; j < 3; j++)
                this[j] = this[j + 1];
            this[3] = 0;
            moved = true;
        }

        return moved ? gained : -1;
    }
}

/// <summary>The 4x4 board. Cells hold exponents (0 empty, 1 = "2", ... 11 = "2048").</summary>
class Board
{
    private readonly byte[] _cells = new byte[16];

    public ushort Score { get; private set; }

    public byte this[int col, int row]
    {
        get => _cells[row * 4 + col];
        set => _cells[row * 4 + col] = value;
    }

    public void Reset()
    {
        for (int i = 0; i < 16; i++)
            _cells[i] = 0;
        Score = 0;
        SpawnTile();
        SpawnTile();
    }

    /// <summary>Slide the whole board in <paramref name="dir"/>. True if anything moved.</summary>
    public bool Slide(Direction dir)
    {
        bool moved = false;
        for (int i = 0; i < 4; i++)
        {
            Line line = ReadLine(dir, i);
            int gained = line.Compact();
            if (gained >= 0)
            {
                moved = true;
                Score += (ushort)gained;
                WriteLine(dir, i, line);
            }
        }
        return moved;
    }

    /// <summary>Place a "2" (or, 1 time in 10, a "4") in a uniformly random empty cell.</summary>
    public void SpawnTile()
    {
        byte free = 0;
        for (int i = 0; i < 16; i++)
            if (_cells[i] == 0)
                free++;
        if (free == 0)
            return;

        byte target = Rng.Next(free);
        for (int i = 0; i < 16; i++)
        {
            if (_cells[i] != 0)
                continue;
            if (target == 0)
            {
                _cells[i] = Rng.Chance(26) ? (byte)2 : (byte)1;
                return;
            }
            target--;
        }
    }

    public bool HasWon()
    {
        for (int i = 0; i < 16; i++)
            if (_cells[i] >= 11)
                return true;
        return false;
    }

    public bool CanMove()
    {
        for (int i = 0; i < 16; i++)
            if (_cells[i] == 0)
                return true;
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 4; col++)
            {
                if (col < 3 && this[col, row] == this[col + 1, row])
                    return true;
                if (row < 3 && this[col, row] == this[col, row + 1])
                    return true;
            }
        }
        return false;
    }

    private Line ReadLine(Direction dir, int i)
    {
        Line line = default;
        for (int j = 0; j < 4; j++)
        {
            line[j] = dir switch
            {
                Direction.Left => this[j, i],
                Direction.Right => this[3 - j, i],
                Direction.Up => this[i, j],
                _ => this[i, 3 - j],
            };
        }
        return line;
    }

    private void WriteLine(Direction dir, int i, Line line)
    {
        for (int j = 0; j < 4; j++)
        {
            byte v = line[j];
            switch (dir)
            {
                case Direction.Left:
                    this[j, i] = v;
                    break;
                case Direction.Right:
                    this[3 - j, i] = v;
                    break;
                case Direction.Up:
                    this[i, j] = v;
                    break;
                default:
                    this[i, 3 - j] = v;
                    break;
            }
        }
    }
}
