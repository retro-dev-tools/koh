namespace Koh.GameBoy.Framework;

/// <summary>
/// A game screen/state: Title, Play, GameOver, a menu — an ordinary class whose overridden
/// <see cref="Update"/> runs once per frame while it is current. Scenes are arena-allocated like
/// any class (<c>Game.ChangeScene(new PlayScene())</c>); state flows between scenes through
/// constructors (<c>new EndScene("GAME OVER", score)</c>) like any C#.
///
/// On a ROM the per-frame <c>scene.Update()</c> is ONE closed-world tag dispatch (a byte load plus
/// a jump-table switch of direct calls — see the compiler's <c>CilVirtualDispatch</c>); everything
/// else about a scene is ordinary devirtualized code. <see cref="Enter"/> runs inside the frame
/// boundary that committed the change (screen on — draw through the deferred <c>Bg</c>/<c>Text</c>
/// shadows, or call <c>Video.Stop</c>/<c>Video.Start</c> yourself for a full re-author);
/// <see cref="Exit"/> runs just before the next scene's Enter.
/// </summary>
public abstract class Scene
{
    /// <summary>One-time setup when this scene becomes current (draw the layout, spawn state).</summary>
    public virtual void Enter() { }

    /// <summary>One frame of this scene's logic. Runs between input latch and frame flush.</summary>
    public abstract void Update();

    /// <summary>Teardown when leaving (typically <c>Bg.Clear(0)</c>).</summary>
    public virtual void Exit() { }
}
