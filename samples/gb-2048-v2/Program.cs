// The whole entry point. Game.Run owns boot and the frame loop: Video.Init, seed Rng, enter the
// first scene, Video.Start, then forever { scene.Update(); Video.EndFrame; latch Input; tick
// Clock; commit any pending ChangeScene }.
using Koh.GameBoy.Framework;

namespace Koh.Samples.Gb2048V2;

static class Program
{
    static void Main() => Game.Run(new TitleScene());
}
