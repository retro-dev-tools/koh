namespace KohUI.Tests;

public class RunnerTests
{
    private sealed class View : IView<int>
    {
        public RenderNode Render() => RenderNode.Leaf("Label");
    }

    [Test]
    public async Task Dispatch_Acknowledges_An_Unchanged_Render()
    {
        await using var runner = new Runner<int, int>(0, (_, model) => model, _ => new View());
        var processed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int patches = 0;
        runner.OnPatchesReady += _ => patches++;
        runner.Dispatch(1, () => processed.SetResult());
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(runner.CurrentModel).IsEqualTo(0);
        await Assert.That(patches).IsEqualTo(0);
    }
}
