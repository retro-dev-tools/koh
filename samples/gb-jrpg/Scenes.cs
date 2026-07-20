// The scene graph: overworld walking with collision + NPC talk + random encounters, a paged
// dialogue box with a close callback, a menu-driven turn-based battle against the enemy hierarchy,
// and victory/defeat endings. All scene state flows through constructors; the hero persists by
// being passed along.
using Koh.GameBoy;
using Koh.GameBoy.Framework;
using Koh.GameBoy.Graphics;

namespace Koh.Samples.GbJrpg;

/// <summary>Drawing helpers for 16x16 figures — a figure is a 2x2 tile block whose sheet is 4
/// tiles across, so from the top-left tile id: TR = +1, BL = +4, BR = +5 (see Assets' id table).</summary>
static class Scenery
{
    /// <summary>Draw a figure at a WORLD CELL (16x16 grid; the map starts at tile row 2).</summary>
    public static void DrawFigure(byte topLeftTile, int cellX, int cellY, byte palette) =>
        DrawFigureAt(topLeftTile, (byte)(cellX * 2), (byte)(cellY * 2 + 2), palette);

    /// <summary>Draw a figure at a raw tilemap position (battle layouts).</summary>
    public static void DrawFigureAt(byte topLeftTile, byte col, byte row, byte palette)
    {
        Bg.SetTile(col, row, topLeftTile);
        Bg.SetTile((byte)(col + 1), row, (byte)(topLeftTile + 1));
        Bg.SetTile(col, (byte)(row + 1), (byte)(topLeftTile + 4));
        Bg.SetTile((byte)(col + 1), (byte)(row + 1), (byte)(topLeftTile + 5));
        Bg.FillAttr(col, row, 2, 2, palette);
    }
}

/// <summary>The classic JRPG window: a double-line frame from the ui sheet around a flat fill,
/// all on the UI palette. Interior is (w-2)x(h-2) starting at (col+1, row+1).</summary>
static class Ui
{
    public static void DrawWindow(byte col, byte row, byte w, byte h)
    {
        byte right = (byte)(col + w - 1),
            bottom = (byte)(row + h - 1);
        Bg.SetTile(col, row, Assets.UiTopLeft);
        Bg.SetTile(right, row, Assets.UiTopRight);
        Bg.SetTile(col, bottom, Assets.UiBottomLeft);
        Bg.SetTile(right, bottom, Assets.UiBottomRight);
        Bg.Fill((byte)(col + 1), row, (byte)(w - 2), 1, Assets.UiTop);
        Bg.Fill((byte)(col + 1), bottom, (byte)(w - 2), 1, Assets.UiBottom);
        Bg.Fill(col, (byte)(row + 1), 1, (byte)(h - 2), Assets.UiLeft);
        Bg.Fill(right, (byte)(row + 1), 1, (byte)(h - 2), Assets.UiRight);
        Bg.Fill((byte)(col + 1), (byte)(row + 1), (byte)(w - 2), (byte)(h - 2), Assets.WindowFill);
        Bg.FillAttr(col, row, w, h, Assets.UiPal);
    }
}

class OverworldScene : Scene
{
    public static Hero Hero = new Hero();
    private static readonly Villager Npc = new Villager();

    public override void Enter()
    {
        // Full re-author with the LCD off (the classic transition blink): attribute writes are
        // direct-to-VRAM and instant off-screen, and nothing stale (a battle layout, a dialogue
        // box) can survive into the fresh scene.
        Video.Stop();
        for (byte y = 0; y < World.Height; y++)
        for (byte x = 0; x < World.Width; x++)
            DrawCell(x, y);
        Scenery.DrawFigure(Assets.NpcTile, Npc.X, Npc.Y, Assets.CharsPal);
        DrawHud();
        Scenery.DrawFigure(Assets.HeroTile, Hero.X, Hero.Y, Assets.CharsPal);
        Video.Start();
    }

    public override void Update()
    {
        int dx = 0,
            dy = 0;
        if (Input.Repeated(Button.Left))
            dx = -1;
        else if (Input.Repeated(Button.Right))
            dx = 1;
        else if (Input.Repeated(Button.Up))
            dy = -1;
        else if (Input.Repeated(Button.Down))
            dy = 1;

        if (Input.Pressed(Button.A) && IsFacingNpc())
        {
            Npc.Interact();
            return;
        }

        if (dx == 0 && dy == 0)
            return;

        int nx = Hero.X + dx,
            ny = Hero.Y + dy;
        if ((nx == Npc.X && ny == Npc.Y) || !World.IsWalkable(nx, ny))
            return;

        DrawCell((byte)Hero.X, (byte)Hero.Y); // restore the vacated cell's terrain block
        Hero.X = nx;
        Hero.Y = ny;
        Scenery.DrawFigure(Assets.HeroTile, nx, ny, Assets.CharsPal);

        if (World.RollEncounter())
        {
            byte roll = Rng.Next(16);
            Enemy foe;
            if (roll < 6)
                foe = new Slime();
            else if (roll < 11)
                foe = new Bat();
            else if (roll < 15)
                foe = new Ghost();
            else
                foe = new Drake();
            Game.ChangeScene(new BattleScene(Hero, foe));
        }
    }

    private bool IsFacingNpc()
    {
        int distX = Hero.X - Npc.X,
            distY = Hero.Y - Npc.Y;
        if (distX < 0)
            distX = -distX;
        if (distY < 0)
            distY = -distY;
        return distX + distY == 1;
    }

    private static void DrawCell(byte x, byte y)
    {
        byte cell = World.TileAt(x, y);
        byte col = (byte)(x * 2),
            row = (byte)(y * 2 + 2);

        if (cell == World.TreeCell)
        {
            // The tree is a real 16x16 block from a 2-tile-across sheet: TL, +1 / +2, +3.
            Bg.SetTile(col, row, Assets.Tree);
            Bg.SetTile((byte)(col + 1), row, (byte)(Assets.Tree + 1));
            Bg.SetTile(col, (byte)(row + 1), (byte)(Assets.Tree + 2));
            Bg.SetTile((byte)(col + 1), (byte)(row + 1), (byte)(Assets.Tree + 3));
            Bg.FillAttr(col, row, 2, 2, Assets.TreePal);
            return;
        }

        if (cell == World.WallCell || cell == World.WaterCell)
        {
            // Checker the sheet's two variants so adjacent blocks read as continuous masonry
            // (or rippling water) instead of a stamped repeat.
            byte baseTile = cell == World.WallCell ? Assets.Wall : Assets.Water;
            Bg.SetTile(col, row, baseTile);
            Bg.SetTile((byte)(col + 1), row, (byte)(baseTile + 1));
            Bg.SetTile(col, (byte)(row + 1), (byte)(baseTile + 1));
            Bg.SetTile((byte)(col + 1), (byte)(row + 1), baseTile);
            Bg.FillAttr(col, row, 2, 2, cell == World.WallCell ? Assets.WallPal : Assets.WaterPal);
            return;
        }

        // Grass: scatter the 4 texture variants per 8x8 subtile — mostly plain, an occasional
        // tuft — hashed off the tile position so the field is organic but deterministic.
        for (byte dy = 0; dy < 2; dy++)
        for (byte dxx = 0; dxx < 2; dxx++)
        {
            byte h = (byte)(((col + dxx) * 7 + (row + dy) * 13) & 7);
            byte tile = h < 5 ? Assets.Grass : (byte)(Assets.Grass + h - 4);
            Bg.SetTile((byte)(col + dxx), (byte)(row + dy), tile);
        }
        Bg.FillAttr(col, row, 2, 2, Assets.GrassPal);
    }

    private static void DrawHud()
    {
        // A parchment status bar over the two rows above the map, closed by a border line.
        Bg.Fill(0, 0, 20, 1, Assets.WindowFill);
        Bg.Fill(0, 1, 20, 1, Assets.UiBottom);
        Bg.FillAttr(0, 0, 20, 2, Assets.UiPal);
        Text.Draw(1, 0, "HP");
        Text.DrawNumber(3, 0, (ushort)Hero.Hp, 3);
        Text.Draw(8, 0, "LV");
        Text.DrawNumber(10, 0, (ushort)Hero.Level, 2);
    }
}

class DialogueScene : Scene
{
    private readonly string[] _lines;
    private readonly System.Action _onClosed;
    private int _page;

    public DialogueScene(string[] lines, System.Action onClosed)
    {
        _lines = lines;
        _onClosed = onClosed;
    }

    public override void Enter()
    {
        // A dialogue is an OVERLAY on the current screen — a framed window over the bottom rows;
        // the next scene's own Enter repaints everything when it closes.
        Ui.DrawWindow(0, 13, 20, 5);
        Text.Draw(1, 14, _lines[0]);
        Bg.SetTile(18, 16, Assets.MoreArrow);
    }

    public override void Update()
    {
        if (!Input.Pressed(Button.A))
            return;
        _page++;
        if (_page < _lines.Length)
        {
            Bg.Fill(1, 14, 18, 1, Assets.WindowFill);
            Text.Draw(1, 14, _lines[_page]);
        }
        else
        {
            _onClosed();
        }
    }
}

class BattleScene : Scene
{
    private readonly Hero _hero;
    private readonly Enemy _enemy;
    private int _cursor; // 0 attack, 1 heal, 2 run
    private byte _phase; // 0 choosing, 1 enemy turn pending, 2 resolved (leaving)

    public BattleScene(Hero hero, Enemy enemy)
    {
        _hero = hero;
        _enemy = enemy;
        enemy.Hp = enemy.MaxHp;
    }

    public override void Enter()
    {
        Video.Stop();
        Bg.Clear(Assets.WindowFill); // parchment battle backdrop — the monsters' own background
        Bg.FillAttr(0, 0, 32, 18, Assets.UiPal);
        Bg.Fill(0, 1, 20, 1, Assets.UiBottom); // close the stats bar like the overworld HUD
        Scenery.DrawFigureAt(_enemy.Tile, 9, 4, Assets.MonsterPal);
        Text.Draw(8, 7, _enemy.Name);
        Ui.DrawWindow(1, 9, 10, 5);
        Text.Draw(4, 10, "ATTACK");
        Text.Draw(4, 11, "HEAL");
        Text.Draw(4, 12, "RUN");
        DrawStats();
        DrawCursor();
        Video.Start();
    }

    public override void Update()
    {
        if (_phase == 2)
            return;

        if (_phase == 1)
        {
            // Enemy acts one frame after the player's action resolved, so both messages land.
            _hero.Hp -= _enemy.RollDamage();
            DrawStats();
            if (_hero.Hp <= 0)
                Game.ChangeScene(new GameOverScene(_hero.Level));
            _phase = 0;
            return;
        }

        if (Input.Pressed(Button.Up) && _cursor > 0)
        {
            _cursor--;
            DrawCursor();
        }
        if (Input.Pressed(Button.Down) && _cursor < 2)
        {
            _cursor++;
            DrawCursor();
        }
        if (!Input.Pressed(Button.A))
            return;

        if (_cursor == 0)
        {
            _enemy.Hp -= _hero.Attack + Rng.Next(3);
            if (_enemy.Hp <= 0)
            {
                _hero.GainExp(_enemy.ExpReward);
                _phase = 2;
                Game.ChangeScene(
                    _hero.Level >= 3 ? new VictoryScene() : (Scene)new OverworldScene()
                );
                return;
            }
            _phase = 1;
        }
        else if (_cursor == 1)
        {
            _hero.Hp = _hero.MaxHp;
            _phase = 1;
        }
        else if (Rng.Chance(160))
        {
            _phase = 2;
            Game.ChangeScene(new OverworldScene());
        }
        else
        {
            _phase = 1; // failed to run — free hit
        }
        DrawStats();
    }

    private void DrawStats()
    {
        Text.Draw(1, 0, "HP");
        Text.DrawNumber(3, 0, (ushort)(_hero.Hp > 0 ? _hero.Hp : 0), 3);
        Text.Draw(8, 0, "FOE");
        Text.DrawNumber(11, 0, (ushort)(_enemy.Hp > 0 ? _enemy.Hp : 0), 3);
    }

    private void DrawCursor()
    {
        for (byte row = 10; row <= 12; row++)
            Bg.SetTile(3, row, Assets.WindowFill);
        Text.Draw(3, (byte)(10 + _cursor), ">");
    }
}

class VictoryScene : Scene
{
    public override void Enter()
    {
        Video.Stop();
        Bg.Clear(Assets.WindowFill);
        Bg.FillAttr(0, 0, 32, 18, Assets.UiPal);
        Ui.DrawWindow(3, 5, 14, 8);
        Text.Draw(5, 7, "YOU ARE A");
        Text.Draw(6, 8, "HERO NOW");
        Text.Draw(4, 11, "START=AGAIN");
        Video.Start();
    }

    public override void Update()
    {
        if (Input.Pressed(Button.Start))
        {
            OverworldScene.Hero = new Hero();
            Game.ChangeScene(new OverworldScene());
        }
    }
}

class GameOverScene : Scene
{
    private readonly int _level;

    public GameOverScene(int level)
    {
        _level = level;
    }

    public override void Enter()
    {
        Video.Stop();
        Bg.Clear(Assets.WindowFill);
        Bg.FillAttr(0, 0, 32, 18, Assets.UiPal);
        Ui.DrawWindow(3, 5, 14, 8);
        Text.Draw(5, 7, "GAME OVER");
        Text.Draw(5, 9, "LEVEL");
        Text.DrawNumber(11, 9, (ushort)_level, 2);
        Text.Draw(4, 11, "START=AGAIN");
        Video.Start();
    }

    public override void Update()
    {
        if (Input.Pressed(Button.Start))
        {
            OverworldScene.Hero = new Hero();
            Game.ChangeScene(new OverworldScene());
        }
    }
}
