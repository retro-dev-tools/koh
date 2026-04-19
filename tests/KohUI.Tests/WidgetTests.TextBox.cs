using KohUI;
using KohUI.Widgets;

namespace KohUI.Tests;

public class TextBoxTests
{
    private abstract record Msg;
    private sealed record SetName(string Value) : Msg;

    [Test]
    public async Task TextBox_Emits_Text_Prop()
    {
        var node = new TextBox<Msg>("hello").Render();
        await Assert.That(node.Type).IsEqualTo("TextBox");
        await Assert.That(node.Props["text"]).IsEqualTo((object)"hello");
    }

    [Test]
    public async Task TextBox_OnChange_Receives_New_Value()
    {
        var node = new TextBox<Msg>("old", OnChange: v => new SetName(v)).Render();
        var handler = node.Props["onChange"] as Func<string, Msg>;
        await Assert.That(handler).IsNotNull();
        var msg = handler!("fresh") as SetName;
        await Assert.That(msg).IsNotNull();
        await Assert.That(msg!.Value).IsEqualTo("fresh");
    }

    [Test]
    public async Task TextBox_Without_Handler_Has_Null_OnChange()
    {
        var node = new TextBox<Msg>("anything").Render();
        await Assert.That(node.Props["onChange"]).IsNull();
    }
}
