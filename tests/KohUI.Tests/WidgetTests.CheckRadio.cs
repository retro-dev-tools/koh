using KohUI;
using KohUI.Widgets;

namespace KohUI.Tests;

public class CheckRadioTests
{
    private abstract record Msg;
    private sealed record SetEnabled(bool Value) : Msg;
    private sealed record PickDmg : Msg;

    [Test]
    public async Task CheckBox_Emits_Checked_Prop_And_Text()
    {
        var node = new CheckBox<Msg>("Enabled", Checked: true).Render();
        await Assert.That(node.Type).IsEqualTo("CheckBox");
        await Assert.That(node.Props["text"]).IsEqualTo((object)"Enabled");
        await Assert.That((bool)node.Props["checked"]!).IsTrue();
    }

    [Test]
    public async Task CheckBox_OnToggle_Receives_Negated_Value()
    {
        // OnToggle is called with the *new* value; if currently checked,
        // clicking should pass false — modelling "toggle" off.
        Func<bool, Msg> onToggle = v => new SetEnabled(v);
        var node = new CheckBox<Msg>("Enabled", Checked: true, OnToggle: onToggle).Render();
        var click = node.Props["onClick"] as Func<Msg>;
        await Assert.That(click).IsNotNull();
        var msg = click!() as SetEnabled;
        await Assert.That(msg).IsNotNull();
        await Assert.That(msg!.Value).IsFalse();
    }

    [Test]
    public async Task CheckBox_Without_Handler_Has_Null_OnClick()
    {
        var node = new CheckBox<Msg>("Enabled", Checked: false).Render();
        await Assert.That(node.Props["onClick"]).IsNull();
    }

    [Test]
    public async Task RadioButton_Emits_Selected_Prop_And_Text()
    {
        var node = new RadioButton<Msg>("DMG", Selected: true, OnSelect: () => new PickDmg()).Render();
        await Assert.That(node.Type).IsEqualTo("RadioButton");
        await Assert.That(node.Props["text"]).IsEqualTo((object)"DMG");
        await Assert.That((bool)node.Props["selected"]!).IsTrue();
        await Assert.That(node.Props["onClick"]).IsTypeOf<Func<Msg>>();
    }
}
