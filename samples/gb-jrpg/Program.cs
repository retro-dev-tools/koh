// Boot into the overworld. Assets load inside the first scene's Enter (LCD still off).
using Koh.GameBoy.Framework;

namespace Koh.Samples.GbJrpg;

static class Program
{
    static void Main() => Game.Run(new BootScene());
}

/// <summary>One-shot boot scene: loads assets while the LCD is off, then hands over.</summary>
class BootScene : Scene
{
    public override void Enter() => Assets.Load();

    public override void Update() => Game.ChangeScene(new OverworldScene());
}
