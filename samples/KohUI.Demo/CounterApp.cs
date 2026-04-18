using System.Collections.Immutable;
using KohUI;
using KohUI.Widgets;

namespace KohUI.Demo;

/// <summary>Entire state of the demo is one int and one flag.</summary>
public readonly record struct CounterModel(int Count, bool WindowOpen);

/// <summary>The things a user can do.</summary>
public abstract record CounterMsg;
public sealed record Increment : CounterMsg;
public sealed record Decrement : CounterMsg;
public sealed record Reset : CounterMsg;
public sealed record CloseWindow : CounterMsg;
public sealed record Reopen : CounterMsg;

public static class CounterApp
{
    public static CounterModel Update(CounterMsg msg, CounterModel m) => msg switch
    {
        Increment   => m with { Count = m.Count + 1 },
        Decrement   => m with { Count = m.Count - 1 },
        Reset       => m with { Count = 0 },
        CloseWindow => m with { WindowOpen = false },
        Reopen      => m with { WindowOpen = true, Count = 0 },
        _ => m,
    };

    public static IView<CounterMsg> View(CounterModel m)
    {
        if (!m.WindowOpen)
        {
            return new Stack<CounterMsg, Label<CounterMsg>, Button<CounterMsg>>(
                StackDirection.Vertical,
                new Label<CounterMsg>("Window closed."),
                new Button<CounterMsg>("Reopen", OnClick: () => new Reopen()));
        }

        // Menu: File (stub), Edit (Reset), Help (stub).
        var menu = new MenuBar<CounterMsg>(ImmutableArray.Create(
            new MenuItem<CounterMsg>("&File"),
            new MenuItem<CounterMsg>("&Edit", OnClick: () => new Reset()),
            new MenuItem<CounterMsg>("&Help")));

        // Big count display, sunken-panel style (like the LCD of a Win98 calculator).
        var display = new Panel<CounterMsg, Label<CounterMsg>>(
            PanelBevel.Sunken,
            new Label<CounterMsg>($"Count: {m.Count}"));

        // Two buttons side by side.
        var buttons = new Stack<CounterMsg, Button<CounterMsg>, Button<CounterMsg>>(
            StackDirection.Horizontal,
            new Button<CounterMsg>("-", OnClick: () => new Decrement()),
            new Button<CounterMsg>("+", OnClick: () => new Increment()));

        // Panel wrapping display + buttons so they get chiseled grouping.
        var body = new Panel<CounterMsg,
                             Stack<CounterMsg,
                                   Panel<CounterMsg, Label<CounterMsg>>,
                                   Stack<CounterMsg, Button<CounterMsg>, Button<CounterMsg>>>>(
            PanelBevel.Raised,
            new Stack<CounterMsg,
                      Panel<CounterMsg, Label<CounterMsg>>,
                      Stack<CounterMsg, Button<CounterMsg>, Button<CounterMsg>>>(
                StackDirection.Vertical, display, buttons));

        // Status bar at the bottom.
        var status = new StatusBar<CounterMsg>(ImmutableArray.Create(
            "Ready",
            $"Value: {m.Count}",
            m.Count >= 0 ? "Non-negative" : "Negative"));

        // Full window body: menu + body panel + status.
        var windowBody = new ForEach<CounterMsg>(
            StackDirection.Vertical,
            ImmutableArray.Create<IView<CounterMsg>>(menu, body, status));

        return new Window<CounterMsg, ForEach<CounterMsg>>(
            Title: "Koh Counter",
            Child: windowBody,
            X: 80, Y: 80, Width: 300,
            OnClose: () => new CloseWindow());
    }
}
