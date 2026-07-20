// Boot into the title screen. Assets load inside the first scene's Enter (LCD still off).
using Koh.GameBoy.Framework;
using Koh.GameBoy.Graphics;

namespace Koh.Samples.GbJrpg;

static class Program
{
    static void Main() => Game.Run(new BootScene());
}

/// <summary>One-shot boot scene: loads assets while the LCD is off, then hands over.</summary>
class BootScene : Scene
{
    public override void Enter() => Assets.Load();

    public override void Update() => Game.ChangeScene(new TitleScene());
}

/// <summary>The title card: a framed window on parchment, the slime mascot, PRESS START.</summary>
class TitleScene : Scene
{
    public override void Enter()
    {
        Video.Stop();
        Bg.Clear(Assets.WindowFill);
        Bg.FillAttr(0, 0, 32, 18, Assets.UiPal);
        Ui.DrawWindow(1, 3, 18, 12);
        Text.Draw(5, 5, "TINY  QUEST");
        Text.Draw(3, 7, "A MILLMERE TALE");
        Scenery.DrawFigureAt(Assets.SlimeTile, 9, 9, Assets.MonsterPal);
        Text.Draw(5, 12, "PRESS START");
        Video.Start();
    }

    public override void Update()
    {
        if (Input.Pressed(Button.Start))
            Game.ChangeScene(new OverworldScene());
    }
}
