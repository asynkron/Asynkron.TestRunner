using Xunit;

namespace Asynkron.TestRunner.Tests;

public class IsolateRunnerTests
{
    [Fact]
    public void Constructor_DefaultsToSequentialExecution()
    {
        var runner = new IsolateRunner(["dotnet", "test"]);

        Assert.Equal(1, runner.MaxParallelBatches);
    }

    [Fact]
    public void Constructor_WithParallelism_SetsMaxParallelBatches()
    {
        var runner = new IsolateRunner(["dotnet", "test"], timeoutSeconds: 30, initialFilter: null, maxParallelBatches: 4);

        Assert.Equal(4, runner.MaxParallelBatches);
    }

    [Fact]
    public void Constructor_WithTimeoutStrategy_SetsMaxParallelBatches()
    {
        var strategy = new TimeoutStrategy(TimeoutMode.Fixed, 30);
        var runner = new IsolateRunner(["dotnet", "test"], strategy, initialFilter: null, maxParallelBatches: 8);

        Assert.Equal(8, runner.MaxParallelBatches);
    }

    [Fact]
    public void Constructor_WithZeroParallelism_DefaultsToOne()
    {
        var runner = new IsolateRunner(["dotnet", "test"], timeoutSeconds: 30, initialFilter: null, maxParallelBatches: 0);

        Assert.Equal(1, runner.MaxParallelBatches);
    }

    [Fact]
    public void Constructor_WithNegativeParallelism_DefaultsToOne()
    {
        var runner = new IsolateRunner(["dotnet", "test"], timeoutSeconds: 30, initialFilter: null, maxParallelBatches: -5);

        Assert.Equal(1, runner.MaxParallelBatches);
    }

    [Fact]
    public void Constructor_PreservesTimeoutStrategy()
    {
        var strategy = new TimeoutStrategy(TimeoutMode.Graduated, 15);
        var runner = new IsolateRunner(["dotnet", "test"], strategy, initialFilter: "MyTests", maxParallelBatches: 2);

        Assert.Equal(TimeoutMode.Graduated, runner.TimeoutStrategy.Mode);
        Assert.Equal(15, runner.TimeoutStrategy.GetTimeout(1));
    }

    [Fact]
    public void IsolatedHangingTests_InitiallyEmpty()
    {
        var runner = new IsolateRunner(["dotnet", "test"]);

        Assert.Empty(runner.IsolatedHangingTests);
    }

    [Fact]
    public void FailedBatches_InitiallyEmpty()
    {
        var runner = new IsolateRunner(["dotnet", "test"]);

        Assert.Empty(runner.FailedBatches);
    }
}
