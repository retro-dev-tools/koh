using Koh.Superopt;

// Mine rewrites under two peephole-relevant liveness contexts and print a report.
// Live.All: rewrites always safe (every register and flag preserved).
// Live.AllRegs: flags-dead rewrites (the common byte-scan peephole precondition).
int maxLength = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 2;

foreach (var live in new[] { Live.All, Live.AllRegs })
{
    Console.WriteLine($"== mined rewrites (maxLength={maxLength}, live-out={live}) ==");
    var rewrites = new Miner(maxLength).Mine(live);
    // Biggest byte win first, then biggest cycle win.
    foreach (
        var r in rewrites.OrderByDescending(r => r.BytesSaved).ThenByDescending(r => r.TCyclesSaved)
    )
        Console.WriteLine("  " + RewriteFormatting.Describe(r));
    Console.WriteLine($"  ({rewrites.Count} rewrites)");
    Console.WriteLine();
}
