using Koh.Core.Text;

namespace Koh.Core.Tests.Text;

public class SourceTextTests
{
    [Test]
    public async Task SourceText_FromString()
    {
        var text = SourceText.From("hello\nworld");
        await Assert.That(text.Length).IsEqualTo(11);
        await Assert.That(text[0]).IsEqualTo('h');
    }

    [Test]
    public async Task SourceText_Lines()
    {
        var text = SourceText.From("hello\nworld\n");
        var lines = text.Lines;
        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(lines[0].Start).IsEqualTo(0);
        await Assert.That(lines[1].Start).IsEqualTo(6);
    }

    [Test]
    public async Task SourceText_GetLineIndex()
    {
        var text = SourceText.From("aaa\nbbb\nccc");
        await Assert.That(text.GetLineIndex(0)).IsEqualTo(0);
        await Assert.That(text.GetLineIndex(4)).IsEqualTo(1);
        await Assert.That(text.GetLineIndex(8)).IsEqualTo(2);
    }

    [Test]
    public async Task SourceText_WithChanges()
    {
        var text = SourceText.From("hello world");
        var changed = text.WithChanges(new TextChange(new(5, 1), "_"));
        await Assert.That(changed.ToString()).IsEqualTo("hello_world");
    }
}
