using KohUI;
using KohUI.Widgets;

namespace KohUI.Demo;

/// <summary>The entire state of the counter demo is one int.</summary>
public readonly record struct CounterModel(int Count);

/// <summary>The two things a user can do.</summary>
public abstract record CounterMsg;
public sealed record Increment : CounterMsg;
public sealed record Decrement : CounterMsg;

/// <summary>Pure update + view; the MVU loop calls these.</summary>
public static class CounterApp
{
    public static CounterModel Update(CounterMsg msg, CounterModel m) => msg switch
    {
        Increment => m with { Count = m.Count + 1 },
        Decrement => m with { Count = m.Count - 1 },
        _ => m,
    };

    public static IView<CounterMsg> View(CounterModel m)
    {
        var label = new Label<CounterMsg>($"Count: {m.Count}");
        var inc = new Button<CounterMsg>("Increment", OnClick: () => new Increment());
        var dec = new Button<CounterMsg>("Decrement", OnClick: () => new Decrement());

        // Horizontal row of buttons, above the label.
        var buttons = new Stack<CounterMsg, Button<CounterMsg>, Button<CounterMsg>>(
            StackDirection.Horizontal, dec, inc);

        return new Stack<CounterMsg,
                         Label<CounterMsg>,
                         Stack<CounterMsg, Button<CounterMsg>, Button<CounterMsg>>>(
            StackDirection.Vertical, label, buttons);
    }
}
