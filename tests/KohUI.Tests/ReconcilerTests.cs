using System.Collections.Immutable;
using KohUI;

namespace KohUI.Tests;

public class ReconcilerTests
{
    private static RenderNode Node(string type, params (string, object?)[] props)
        => RenderNode.Leaf(type, Props.Of(props));

    private static RenderNode Container(string type, params RenderNode[] children)
        => RenderNode.WithChildren(type, [.. children]);

    [Test]
    public async Task Diff_From_Null_Emits_Single_ReplaceNode()
    {
        var tree = Node("Label", ("text", "hi"));
        var patches = Reconciler.Diff(previous: null, current: tree);
        await Assert.That(patches.Count).IsEqualTo(1);
        await Assert.That(patches[0]).IsTypeOf<ReplaceNode>();
        await Assert.That(((ReplaceNode)patches[0]).Path).IsEqualTo("");
    }

    [Test]
    public async Task Diff_Identical_Trees_Emits_No_Patches()
    {
        var t1 = Node("Label", ("text", "hi"));
        var t2 = Node("Label", ("text", "hi"));
        var patches = Reconciler.Diff(t1, t2);
        await Assert.That(patches.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Diff_Changed_Prop_Emits_UpdateProps()
    {
        var t1 = Node("Label", ("text", "hi"));
        var t2 = Node("Label", ("text", "hello"));
        var patches = Reconciler.Diff(t1, t2);
        await Assert.That(patches.Count).IsEqualTo(1);
        await Assert.That(patches[0]).IsTypeOf<UpdateProps>();
        var u = (UpdateProps)patches[0];
        await Assert.That(u.Path).IsEqualTo("");
        await Assert.That(u.Changed["text"]).IsEqualTo((object)"hello");
        await Assert.That(u.Removed.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Diff_Different_Type_Emits_ReplaceNode_At_Path()
    {
        var t1 = Container("Stack", Node("Label", ("text", "a")));
        var t2 = Container("Stack", Node("Button", ("text", "a")));
        var patches = Reconciler.Diff(t1, t2);
        await Assert.That(patches.Count).IsEqualTo(1);
        await Assert.That(patches[0]).IsTypeOf<ReplaceNode>();
        await Assert.That(((ReplaceNode)patches[0]).Path).IsEqualTo("0");
    }

    [Test]
    public async Task Diff_Adds_New_Child_As_InsertChild()
    {
        var t1 = Container("Stack", Node("Label", ("text", "a")));
        var t2 = Container("Stack",
            Node("Label", ("text", "a")),
            Node("Label", ("text", "b")));
        var patches = Reconciler.Diff(t1, t2);
        await Assert.That(patches.Count).IsEqualTo(1);
        await Assert.That(patches[0]).IsTypeOf<InsertChild>();
        var ins = (InsertChild)patches[0];
        await Assert.That(ins.Path).IsEqualTo("");
        await Assert.That(ins.Index).IsEqualTo(1);
    }

    [Test]
    public async Task Diff_Removes_Trailing_Child_As_RemoveChild()
    {
        var t1 = Container("Stack",
            Node("Label", ("text", "a")),
            Node("Label", ("text", "b")));
        var t2 = Container("Stack", Node("Label", ("text", "a")));
        var patches = Reconciler.Diff(t1, t2);
        await Assert.That(patches.Count).IsEqualTo(1);
        await Assert.That(patches[0]).IsTypeOf<RemoveChild>();
        var rm = (RemoveChild)patches[0];
        await Assert.That(rm.Index).IsEqualTo(1);
    }

    [Test]
    public async Task Diff_Nested_Prop_Change_Uses_Deep_Path()
    {
        var t1 = Container("Stack",
            Container("Stack", Node("Label", ("text", "old"))));
        var t2 = Container("Stack",
            Container("Stack", Node("Label", ("text", "new"))));
        var patches = Reconciler.Diff(t1, t2);
        await Assert.That(patches.Count).IsEqualTo(1);
        await Assert.That(patches[0]).IsTypeOf<UpdateProps>();
        await Assert.That(((UpdateProps)patches[0]).Path).IsEqualTo("0.0");
    }
}
