using Xunit;

namespace Asynkron.TestRunner.Tests;

public class TimeoutStrategyTests
{
    [Fact]
    public void Fixed_ReturnsBaseTimeout()
    {
        var strategy = new TimeoutStrategy(TimeoutMode.Fixed, 30);

        Assert.Equal(30, strategy.GetTimeout());
        Assert.Equal(30, strategy.GetTimeout(1));
        Assert.Equal(30, strategy.GetTimeout(2));
        Assert.Equal(30, strategy.GetTimeout(3));
    }

    [Fact]
    public void None_ReturnsZero()
    {
        var strategy = new TimeoutStrategy(TimeoutMode.None, 30);

        Assert.Equal(0, strategy.GetTimeout());
        Assert.Equal(0, strategy.GetTimeout(1));
    }

    [Fact]
    public void Graduated_DoublesEachAttempt()
    {
        var strategy = new TimeoutStrategy(TimeoutMode.Graduated, 10);

        Assert.Equal(10, strategy.GetTimeout(1)); // 10 * 2^0 = 10
        Assert.Equal(20, strategy.GetTimeout(2)); // 10 * 2^1 = 20
        Assert.Equal(40, strategy.GetTimeout(3)); // 10 * 2^2 = 40
        Assert.Equal(80, strategy.GetTimeout(4)); // 10 * 2^3 = 80
    }

    [Fact]
    public void Adaptive_FallsBackToBase_WhenNoStore()
    {
        var strategy = new TimeoutStrategy(TimeoutMode.Adaptive, 25, store: null);

        Assert.Equal(25, strategy.GetTimeout());
    }

    [Fact]
    public void GetBatchTimeout_ScalesWithTestCount()
    {
        var strategy = new TimeoutStrategy(TimeoutMode.Fixed, 20);

        var singleTest = strategy.GetBatchTimeout(1);
        var tenTests = strategy.GetBatchTimeout(10);
        var hundredTests = strategy.GetBatchTimeout(100);

        // Batch timeout should scale but not linearly
        Assert.True(singleTest < tenTests);
        Assert.True(tenTests < hundredTests);
        // Should not exceed total possible time
        Assert.True(hundredTests <= 20 * 100);
    }

    [Fact]
    public void GetBatchTimeout_ReturnsZero_WhenModeIsNone()
    {
        var strategy = new TimeoutStrategy(TimeoutMode.None, 20);

        Assert.Equal(0, strategy.GetBatchTimeout(10));
    }

    [Fact]
    public void FromOptions_ParsesFixedMode()
    {
        var strategy = TimeoutStrategy.FromOptions("fixed", 15, null);

        Assert.Equal(TimeoutMode.Fixed, strategy.Mode);
        Assert.Equal(15, strategy.BaseTimeoutSeconds);
    }

    [Fact]
    public void FromOptions_ParsesAdaptiveMode()
    {
        var strategy = TimeoutStrategy.FromOptions("adaptive", 20, null);

        Assert.Equal(TimeoutMode.Adaptive, strategy.Mode);
    }

    [Fact]
    public void FromOptions_ParsesGraduatedMode()
    {
        var strategy = TimeoutStrategy.FromOptions("graduated", 10, null);

        Assert.Equal(TimeoutMode.Graduated, strategy.Mode);
    }

    [Fact]
    public void FromOptions_ParsesNoneMode()
    {
        var strategy = TimeoutStrategy.FromOptions("none", 20, null);

        Assert.Equal(TimeoutMode.None, strategy.Mode);
    }

    [Fact]
    public void FromOptions_ParsesZeroAsNone()
    {
        var strategy = TimeoutStrategy.FromOptions("0", 20, null);

        Assert.Equal(TimeoutMode.None, strategy.Mode);
    }

    [Fact]
    public void FromOptions_DefaultsToFixed()
    {
        var strategy = TimeoutStrategy.FromOptions(null, 20, null);

        Assert.Equal(TimeoutMode.Fixed, strategy.Mode);
    }

    [Fact]
    public void GetDescription_ReturnsReadableString()
    {
        var fixedStrategy = new TimeoutStrategy(TimeoutMode.Fixed, 20);
        var noneStrategy = new TimeoutStrategy(TimeoutMode.None, 20);
        var adaptiveStrategy = new TimeoutStrategy(TimeoutMode.Adaptive, 20);
        var graduatedStrategy = new TimeoutStrategy(TimeoutMode.Graduated, 10);

        Assert.Contains("20s", fixedStrategy.GetDescription());
        Assert.Contains("No timeout", noneStrategy.GetDescription());
        Assert.Contains("Adaptive", adaptiveStrategy.GetDescription());
        Assert.Contains("doubles", graduatedStrategy.GetDescription());
    }
}
