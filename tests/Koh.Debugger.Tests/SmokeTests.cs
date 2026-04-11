namespace Koh.Debugger.Tests;

public class SmokeTests
{
    [Test]
    public async Task Scaffold_Works()
    {
        int value = Sum(1, 1);
        await Assert.That(value).IsEqualTo(2);
    }

    private static int Sum(int a, int b) => a + b;
}
