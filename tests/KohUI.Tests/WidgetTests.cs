using System.Collections.Immutable;
using KohUI;
using KohUI.Widgets;

namespace KohUI.Tests;

public class WidgetTests
{
    // Dummy message type — widgets are Msg-generic but don't need a
    // real discriminated union for render-tree shape tests.
    private abstract record Msg;
    private sealed record Ping : Msg;

    [Test]
    public async Task Label_Renders_As_Label_Type_With_Text_Prop()
    {
        var node = new Label<Msg>("hi").Render();
        await Assert.That(node.Type).IsEqualTo("Label");
        await Assert.That(node.Props["text"]).IsEqualTo((object)"hi");
        await Assert.That(node.Children.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Button_Without_Handler_Renders_Null_onClick()
    {
        var node = new Button<Msg>("Go").Render();
        await Assert.That(node.Type).IsEqualTo("Button");
        await Assert.That(node.Props["onClick"]).IsNull();
    }

    [Test]
    public async Task Button_With_Handler_Carries_Delegate_In_Props()
    {
        var node = new Button<Msg>("Go", OnClick: () => new Ping()).Render();
        await Assert.That(node.Props["onClick"]).IsTypeOf<Func<Msg>>();
    }

    [Test]
    public async Task Stack_Renders_Two_Children_In_Order()
    {
        var stack = new Stack<Msg, Label<Msg>, Button<Msg>>(
            StackDirection.Horizontal,
            new Label<Msg>("a"),
            new Button<Msg>("b"));
        var node = stack.Render();
        await Assert.That(node.Type).IsEqualTo("Stack");
        await Assert.That(node.Props["direction"]).IsEqualTo((object)"Horizontal");
        await Assert.That(node.Children.Length).IsEqualTo(2);
        await Assert.That(node.Children[0].Type).IsEqualTo("Label");
        await Assert.That(node.Children[1].Type).IsEqualTo("Button");
    }

    [Test]
    public async Task Window_Wraps_Child_And_Carries_Geometry()
    {
        var win = new Window<Msg, Label<Msg>>("Test", new Label<Msg>("body"),
            X: 10, Y: 20, Width: 300, Height: 150, OnClose: () => new Ping());
        var node = win.Render();
        await Assert.That(node.Type).IsEqualTo("Window");
        await Assert.That(node.Props["title"]).IsEqualTo((object)"Test");
        await Assert.That(node.Props["x"]).IsEqualTo((object)10);
        await Assert.That(node.Props["width"]).IsEqualTo((object)300);
        await Assert.That(node.Children.Length).IsEqualTo(1);
        await Assert.That(node.Children[0].Type).IsEqualTo("Label");
    }

    [Test]
    public async Task MenuBar_Items_Each_Become_A_MenuItem_Child()
    {
        var menu = new MenuBar<Msg>(ImmutableArray.Create(
            new MenuItem<Msg>("&File"),
            new MenuItem<Msg>("&Help")));
        var node = menu.Render();
        await Assert.That(node.Type).IsEqualTo("MenuBar");
        await Assert.That(node.Children.Length).IsEqualTo(2);
        await Assert.That(node.Children[0].Props["text"]).IsEqualTo((object)"&File");
    }

    [Test]
    public async Task Panel_Records_Bevel_Style_As_String()
    {
        var node = new Panel<Msg, Label<Msg>>(PanelBevel.Chiseled, new Label<Msg>("inner")).Render();
        await Assert.That(node.Type).IsEqualTo("Panel");
        await Assert.That(node.Props["bevel"]).IsEqualTo((object)"Chiseled");
    }

    [Test]
    public async Task StatusBar_Each_Segment_Renders_As_StatusBarSegment_Child()
    {
        var sb = new StatusBar<Msg>(ImmutableArray.Create("Ready", "Value: 42"));
        var node = sb.Render();
        await Assert.That(node.Type).IsEqualTo("StatusBar");
        await Assert.That(node.Children.Length).IsEqualTo(2);
        await Assert.That(node.Children[1].Type).IsEqualTo("StatusBarSegment");
        await Assert.That(node.Children[1].Props["text"]).IsEqualTo((object)"Value: 42");
    }
}
