using System.Collections.Immutable;
using KohUI;
using KohUI.Widgets;

namespace KohUI.Demo;

public readonly record struct CounterModel(int Count, int Step, bool AllowNegative, bool WindowOpen);

public abstract record CounterMsg;
public sealed record Increment : CounterMsg;
public sealed record Decrement : CounterMsg;
public sealed record Reset : CounterMsg;
public sealed record SetStep(int Step) : CounterMsg;
public sealed record SetAllowNegative(bool Allow) : CounterMsg;
public sealed record CloseWindow : CounterMsg;
public sealed record Reopen : CounterMsg;

public static class CounterApp
{
    public static CounterModel Update(CounterMsg msg, CounterModel m) => msg switch
    {
        Increment            => m with { Count = m.Count + m.Step },
        Decrement            => m with { Count = Clamp(m.Count - m.Step, m.AllowNegative) },
        Reset                => m with { Count = 0 },
        SetStep s            => m with { Step = s.Step },
        SetAllowNegative a   => m with { AllowNegative = a.Allow, Count = Clamp(m.Count, a.Allow) },
        CloseWindow          => m with { WindowOpen = false },
        Reopen               => m with { WindowOpen = true, Count = 0 },
        _ => m,
    };

    private static int Clamp(int v, bool allowNegative) => allowNegative ? v : Math.Max(0, v);

    public static IView<CounterMsg> View(CounterModel m)
    {
        if (!m.WindowOpen)
        {
            return new Stack<CounterMsg, Label<CounterMsg>, Button<CounterMsg>>(
                StackDirection.Vertical,
                new Label<CounterMsg>("Window closed."),
                new Button<CounterMsg>("Reopen", OnClick: () => new Reopen()));
        }

        var menu = new MenuBar<CounterMsg>(ImmutableArray.Create(
            new MenuItem<CounterMsg>("&File"),
            new MenuItem<CounterMsg>("&Edit", OnClick: () => new Reset()),
            new MenuItem<CounterMsg>("&Help")));

        var display = new Panel<CounterMsg, Label<CounterMsg>>(
            PanelBevel.Sunken,
            new Label<CounterMsg>($"Count: {m.Count}"));

        var buttons = new Stack<CounterMsg, Button<CounterMsg>, Button<CounterMsg>>(
            StackDirection.Horizontal,
            new Button<CounterMsg>("-", OnClick: () => new Decrement()),
            new Button<CounterMsg>("+", OnClick: () => new Increment()),
            Stretch: true);

        // Radios in a group — exactly one Selected at a time. Handler sets
        // the new step size, update flips the model, re-render makes the
        // right radio visually selected on the next frame.
        var steps = new ForEach<CounterMsg>(
            StackDirection.Horizontal,
            ImmutableArray.Create<IView<CounterMsg>>(
                new Label<CounterMsg>("Step:"),
                new RadioButton<CounterMsg>("1",  m.Step == 1,  OnSelect: () => new SetStep(1)),
                new RadioButton<CounterMsg>("5",  m.Step == 5,  OnSelect: () => new SetStep(5)),
                new RadioButton<CounterMsg>("10", m.Step == 10, OnSelect: () => new SetStep(10))));

        var allowNeg = new CheckBox<CounterMsg>(
            "Allow negative values",
            Checked: m.AllowNegative,
            OnToggle: v => new SetAllowNegative(v));

        var body = new Panel<CounterMsg,
                             ForEach<CounterMsg>>(
            PanelBevel.Raised,
            new ForEach<CounterMsg>(
                StackDirection.Vertical,
                ImmutableArray.Create<IView<CounterMsg>>(display, buttons, steps, allowNeg)));

        var status = new StatusBar<CounterMsg>(ImmutableArray.Create(
            "Ready",
            $"Value: {m.Count}",
            $"Step: {m.Step}"));

        var windowBody = new ForEach<CounterMsg>(
            StackDirection.Vertical,
            ImmutableArray.Create<IView<CounterMsg>>(menu, body, status));

        return new Window<CounterMsg, ForEach<CounterMsg>>(
            Title: "Koh Counter",
            Child: windowBody,
            X: 80, Y: 80, Width: 320,
            OnClose: () => new CloseWindow());
    }
}
