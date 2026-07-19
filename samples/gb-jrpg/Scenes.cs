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
            Game.ChangeScene(
                new BattleScene(Hero, Rng.Chance(128) ? new Slime() : (Enemy)new Bat())
            );
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
        byte tile = World.TileAt(x, y);
        byte col = (byte)(x * 2),
            row = (byte)(y * 2 + 2);
        Bg.Fill(col, row, 2, 2, tile);
        Bg.FillAttr(col, row, 2, 2, Assets.PaletteFor(tile));
    }

    private static void DrawHud()
    {
        Text.Draw(0, 0, "HP");
        Text.DrawNumber(2, 0, (ushort)Hero.Hp, 3);
        Text.Draw(6, 0, "LV");
        Text.DrawNumber(8, 0, (ushort)Hero.Level, 2);
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
        // A dialogue is an OVERLAY on the current screen — no re-author; the box rows are cleared
        // to blank and the next scene's own Enter repaints everything when it closes.
        Bg.Fill(0, 13, 20, 5, Assets.Blank);
        Bg.FillAttr(0, 13, 20, 5, Assets.GrassPal);
        Text.Draw(1, 14, _lines[0]);
        Text.Draw(17, 16, "A>");
    }

    public override void Update()
    {
        if (!Input.Pressed(Button.A))
            return;
        _page++;
        if (_page < _lines.Length)
        {
            Bg.Fill(1, 14, 18, 1, Assets.Blank);
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
        Bg.Clear(Assets.Blank);
        Bg.FillAttr(0, 0, 32, 18, Assets.GrassPal); // reset stale overworld attributes
        Text.Draw(3, 3, _enemy.Name);
        Scenery.DrawFigureAt(_enemy.Tile, 9, 5, Assets.MonsterPal); // the 16x16 monster, colored
        Text.Draw(2, 10, "ATTACK");
        Text.Draw(2, 11, "HEAL");
        Text.Draw(2, 12, "RUN");
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
            Bg.SetTile(1, row, Assets.Blank);
        Text.Draw(1, (byte)(10 + _cursor), ">");
    }
}

class VictoryScene : Scene
{
    public override void Enter()
    {
        Video.Stop();
        Bg.Clear(Assets.Blank);
        Bg.FillAttr(0, 0, 32, 18, Assets.GrassPal);
        Text.Draw(5, 8, "YOU ARE A");
        Text.Draw(6, 9, "HERO NOW");
        Text.Draw(3, 12, "START = AGAIN");
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
        Bg.Clear(Assets.Blank);
        Bg.FillAttr(0, 0, 32, 18, Assets.GrassPal);
        Text.Draw(5, 8, "GAME OVER");
        Text.Draw(4, 10, "LEVEL");
        Text.DrawNumber(10, 10, (ushort)_level, 2);
        Text.Draw(3, 12, "START = AGAIN");
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
