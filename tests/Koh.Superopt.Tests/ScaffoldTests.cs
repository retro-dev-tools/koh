namespace Koh.Superopt.Tests;

public class ScaffoldTests
{
    [Test]
    public async Task Scaffold_builds_and_runs()
    {
        await Assert.That(Guid.NewGuid()).IsNotEqualTo(Guid.Empty);
    }
}
