using Koh.Superopt;

namespace Koh.Superopt.Tests;

public class RewriteFormattingTests
{
    [Test]
    public async Task Describes_a_rewrite_with_hex_and_savings()
    {
        var r = new Rewrite([0x3E, 0x00], [0xAF], Live.AllRegs, 1, 4);
        var line = RewriteFormatting.Describe(r);
        await Assert.That(line).Contains("3E 00");
        await Assert.That(line).Contains("AF");
        await Assert.That(line).Contains("-1 byte");
    }

    [Test]
    public async Task Describes_a_deletion_as_removed()
    {
        var r = new Rewrite([0xB7], [], Live.AllRegs, 1, 4);
        await Assert.That(RewriteFormatting.Describe(r)).Contains("(removed)");
    }
}
