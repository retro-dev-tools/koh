namespace Koh.GameBoy;

/// <summary>
/// Marks a method as a Game Boy interrupt handler wired to a hardware vector (e.g.
/// <c>[Interrupt("VBlank")]</c>). The Koh compiler emits it at the vector address with an <c>RETI</c>
/// epilogue; under the plain .NET SDK the attribute is inert (the reference run has no interrupts),
/// so a game that declares handlers still compiles and runs on the desktop.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class InterruptAttribute : Attribute
{
    public InterruptAttribute(string kind) => Kind = kind;

    /// <summary>The interrupt kind: VBlank, Stat/LcdStat/Lcd, Timer, Serial, or Joypad.</summary>
    public string Kind { get; }
}
