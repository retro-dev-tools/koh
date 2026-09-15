// The scene layer, the ideal way: ordinary classes deriving from Framework's abstract Scene, with
// overridden Enter/Update/Exit (compiler enabler E2: prefix field layout + closed-world
// devirtualization). Scene-to-scene state flows through constructors like any C# — EndScene carries
// a message string and the final score as readonly fields.
using Koh.GameBoy;
using Koh.GameBoy.Framework;
using Koh.GameBoy.Graphics;

namespace Koh.Samples.Gb2048V2;

class TitleScene : Scene
{
    public override void Enter()
    {
        Assets.Load();
        Text.Draw(8, 7, "2048");
        Text.Draw(4, 10, "PRESS START");
    }

    public override void Update()
    {
        if (Input.Pressed(Button.Start))
        {
            Rng.Mix(Hardware.DIV); // human timing entropy — the sample's one raw-layer touch
            Game.ChangeScene(new PlayScene());
        }
    }

    public override void Exit() => Bg.Clear(0);
}

class PlayScene : Scene
{
    private readonly Board _board = new Board();

    public override void Enter()
    {
        _board.Reset();
        Text.Draw(1, 0, "SCORE");
        Text.DrawNumber(7, 0, _board.Score, 5);
        BoardView.Draw(_board);
    }

    public override void Update()
    {
        if (!TryReadDirection(out Direction dir))
            return;
        if (!_board.Slide(dir))
            return;

        _board.SpawnTile();
        BoardView.Draw(_board);
        Text.DrawNumber(7, 0, _board.Score, 5);

        if (_board.HasWon())
            Game.ChangeScene(new EndScene("YOU WIN!", _board.Score));
        else if (!_board.CanMove())
            Game.ChangeScene(new EndScene("GAME OVER", _board.Score));
    }

    public override void Exit() => Bg.Clear(0);

    private static bool TryReadDirection(out Direction dir)
    {
        dir = Direction.Left;
        if (Input.Repeated(Button.Left))
            dir = Direction.Left;
        else if (Input.Repeated(Button.Right))
            dir = Direction.Right;
        else if (Input.Repeated(Button.Up))
            dir = Direction.Up;
        else if (Input.Repeated(Button.Down))
            dir = Direction.Down;
        else
            return false;
        return true;
    }
}

class EndScene : Scene
{
    private readonly string _message;
    private readonly ushort _score;

    public EndScene(string message, ushort score)
    {
        _message = message;
        _score = score;
    }

    public override void Enter()
    {
        Text.Draw(5, 7, _message);
        Text.Draw(5, 9, "SCORE");
        Text.DrawNumber(11, 9, _score, 5);
        Text.Draw(3, 12, "START = AGAIN");
    }

    public override void Update()
    {
        if (Input.Pressed(Button.Start))
            Game.ChangeScene(new PlayScene());
    }

    public override void Exit() => Bg.Clear(0);
}
