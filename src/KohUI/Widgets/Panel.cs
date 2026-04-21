using System.Collections.Immutable;

namespace KohUI.Widgets;

/// <summary>
/// Bevel style for <see cref="Panel{TMsg, TChild}"/>. Matches 98.css's
/// three canonical inset/outset/etched looks.
/// </summary>
public enum PanelBevel
{
    /// <summary>Recessed look — typical for content areas, list backgrounds, text boxes.</summary>
    Sunken,
    /// <summary>Raised look — typical for toolbar buttons, status segments.</summary>
    Raised,
    /// <summary>Etched double-line — typical for group-box separators and "field" dividers.</summary>
    Chiseled,
}

/// <summary>
/// Bevelled container. Visual grouping with no interactive behaviour
/// of its own.
/// </summary>
public readonly struct Panel<TMsg, TChild>(PanelBevel Bevel, TChild Child) : IView<TMsg>
    where TChild : IView<TMsg>
{
    public readonly PanelBevel Bevel = Bevel;
    public readonly TChild Child = Child;

    public RenderNode Render()
        => RenderNode.WithChildren("Panel",
            ImmutableArray.Create(Child.Render()),
            Props.Of(("bevel", Bevel.ToString())));
}
