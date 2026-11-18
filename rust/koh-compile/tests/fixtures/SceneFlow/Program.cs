using Koh.GameBoy;
using Koh.GameBoy.Framework;

sealed class TitleScene : Scene
{
    public override void Enter() => Program.Record(1);

    public override void Update()
    {
        if (Clock.Frames < 2)
            return;
        Program.Record(2);
        Game.ChangeScene(new PlayScene(1234));
        Program.Record(3);
    }

    public override void Exit() => Program.Record(4);
}

sealed class PlayScene : Scene
{
    readonly ushort code;

    public PlayScene(ushort code) => this.code = code;

    public override void Enter()
    {
        Program.Record(5);
        Program.State = code;
    }

    public override void Update()
    {
        Program.Record(6);
        Game.ChangeScene(new EndScene(Program.State));
        Program.Record(7);
    }

    public override void Exit() => Program.Record(8);
}

sealed class EndScene : Scene
{
    readonly ushort code;

    public EndScene(ushort code) => this.code = code;

    public override void Enter()
    {
        Program.Record(9);
        Program.Publish(code, Clock.Frames);
        Program.Done();
    }

    public override void Update() { }
}

public static class Program
{
    public static ushort State;
    static uint trace;

    public static void Record(byte value) => trace = trace * 10 + value;

    public static unsafe void Publish(ushort code, uint frames)
    {
        *(uint*)0xC008 = trace;
        *(ushort*)0xC00C = code;
        *(ushort*)0xC00E = (ushort)frames;
    }

    public static void Done() { }

    public static void Main()
    {
        Hardware.LCDC = 0;
        Game.Run(new TitleScene());
    }
}
