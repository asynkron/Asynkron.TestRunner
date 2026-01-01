using Asynkron.TestRunner.Models;
using Xunit;

namespace Asynkron.TestRunner.Tests;

public class TestRunResultTests
{
    [Fact]
    public void Total_ReturnsCorrectSum()
    {
        var result = CreateResult(passed: 5, failed: 3, skipped: 2);
        Assert.Equal(10, result.Total);
    }

    [Fact]
    public void PassRate_CalculatesCorrectly()
    {
        var result = CreateResult(passed: 80, failed: 20, skipped: 0);
        Assert.Equal(80.0, result.PassRate, 0.01);
    }

    [Fact]
    public void PassRate_WithZeroTotal_ReturnsZero()
    {
        var result = CreateResult(passed: 0, failed: 0, skipped: 0);
        Assert.Equal(0, result.PassRate);
    }

    [Fact]
    public void GetRegressions_FindsTestsThatWerePassingNowFailing()
    {
        var previous = CreateResult(passed: 3, failed: 1, skipped: 0);
        previous.PassedTests = ["Test1", "Test2", "Test3"];
        previous.FailedTests = ["Test4"];

        var current = CreateResult(passed: 2, failed: 2, skipped: 0);
        current.PassedTests = ["Test1", "Test3"];
        current.FailedTests = ["Test2", "Test4"];

        var regressions = current.GetRegressions(previous);

        Assert.Single(regressions);
        Assert.Contains("Test2", regressions);
    }

    [Fact]
    public void GetRegressions_NoPrevious_ReturnsEmpty()
    {
        var current = CreateResult(passed: 2, failed: 2, skipped: 0);
        current.FailedTests = ["Test1", "Test2"];

        var regressions = current.GetRegressions(null);

        Assert.Empty(regressions);
    }

    [Fact]
    public void GetFixes_FindsTestsThatWereFailingNowPassing()
    {
        var previous = CreateResult(passed: 2, failed: 2, skipped: 0);
        previous.PassedTests = ["Test1", "Test2"];
        previous.FailedTests = ["Test3", "Test4"];

        var current = CreateResult(passed: 3, failed: 1, skipped: 0);
        current.PassedTests = ["Test1", "Test2", "Test3"];
        current.FailedTests = ["Test4"];

        var fixes = current.GetFixes(previous);

        Assert.Single(fixes);
        Assert.Contains("Test3", fixes);
    }

    [Fact]
    public void GetFixes_NoPrevious_ReturnsEmpty()
    {
        var current = CreateResult(passed: 2, failed: 0, skipped: 0);
        current.PassedTests = ["Test1", "Test2"];

        var fixes = current.GetFixes(null);

        Assert.Empty(fixes);
    }

    private static TestRunResult CreateResult(int passed, int failed, int skipped)
    {
        return new TestRunResult
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = DateTime.Now,
            Passed = passed,
            Failed = failed,
            Skipped = skipped,
            Duration = TimeSpan.FromSeconds(10)
        };
    }
}
