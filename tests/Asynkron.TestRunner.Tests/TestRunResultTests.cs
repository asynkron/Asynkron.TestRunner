using Asynkron.TestRunner.Models;
using Xunit;

namespace Asynkron.TestRunner.Tests;

public class TestRunResultTests
{
    [Fact]
    public void Total_SumOfAllCounts()
    {
        var result = new TestRunResult
        {
            Id = "test",
            Timestamp = DateTime.Now,
            Passed = 10,
            Failed = 2,
            Skipped = 3,
            Duration = TimeSpan.FromSeconds(5)
        };

        Assert.Equal(15, result.Total);
    }

    [Fact]
    public void PassRate_CalculatesCorrectPercentage()
    {
        var result = new TestRunResult
        {
            Id = "test",
            Timestamp = DateTime.Now,
            Passed = 80,
            Failed = 10,
            Skipped = 10,
            Duration = TimeSpan.FromSeconds(5)
        };

        Assert.Equal(80.0, result.PassRate);
    }

    [Fact]
    public void PassRate_ZeroTotal_ReturnsZero()
    {
        var result = new TestRunResult
        {
            Id = "test",
            Timestamp = DateTime.Now,
            Passed = 0,
            Failed = 0,
            Skipped = 0,
            Duration = TimeSpan.Zero
        };

        Assert.Equal(0, result.PassRate);
    }

    [Fact]
    public void GetRegressions_FindsTestsThatPassedBeforeButFailNow()
    {
        var previous = new TestRunResult
        {
            Id = "prev",
            Timestamp = DateTime.Now.AddMinutes(-5),
            Passed = 3,
            Failed = 0,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            PassedTests = ["Test1", "Test2", "Test3"]
        };

        var current = new TestRunResult
        {
            Id = "curr",
            Timestamp = DateTime.Now,
            Passed = 2,
            Failed = 1,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            PassedTests = ["Test1", "Test2"],
            FailedTests = ["Test3"]
        };

        var regressions = current.GetRegressions(previous);

        Assert.Single(regressions);
        Assert.Contains("Test3", regressions);
    }

    [Fact]
    public void GetRegressions_NullPrevious_ReturnsEmpty()
    {
        var current = new TestRunResult
        {
            Id = "curr",
            Timestamp = DateTime.Now,
            Passed = 2,
            Failed = 1,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            FailedTests = ["Test3"]
        };

        var regressions = current.GetRegressions(null);

        Assert.Empty(regressions);
    }

    [Fact]
    public void GetFixes_FindsTestsThatFailedBeforeButPassNow()
    {
        var previous = new TestRunResult
        {
            Id = "prev",
            Timestamp = DateTime.Now.AddMinutes(-5),
            Passed = 2,
            Failed = 1,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            PassedTests = ["Test1", "Test2"],
            FailedTests = ["Test3"]
        };

        var current = new TestRunResult
        {
            Id = "curr",
            Timestamp = DateTime.Now,
            Passed = 3,
            Failed = 0,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            PassedTests = ["Test1", "Test2", "Test3"]
        };

        var fixes = current.GetFixes(previous);

        Assert.Single(fixes);
        Assert.Contains("Test3", fixes);
    }

    [Fact]
    public void GetFixes_NullPrevious_ReturnsEmpty()
    {
        var current = new TestRunResult
        {
            Id = "curr",
            Timestamp = DateTime.Now,
            Passed = 3,
            Failed = 0,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            PassedTests = ["Test1", "Test2", "Test3"]
        };

        var fixes = current.GetFixes(null);

        Assert.Empty(fixes);
    }

    [Fact]
    public void GetRegressions_NoChanges_ReturnsEmpty()
    {
        var previous = new TestRunResult
        {
            Id = "prev",
            Timestamp = DateTime.Now.AddMinutes(-5),
            Passed = 3,
            Failed = 0,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            PassedTests = ["Test1", "Test2", "Test3"]
        };

        var current = new TestRunResult
        {
            Id = "curr",
            Timestamp = DateTime.Now,
            Passed = 3,
            Failed = 0,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            PassedTests = ["Test1", "Test2", "Test3"]
        };

        var regressions = current.GetRegressions(previous);
        var fixes = current.GetFixes(previous);

        Assert.Empty(regressions);
        Assert.Empty(fixes);
    }

    [Fact]
    public void GetRegressions_NewFailingTest_NotARegression()
    {
        var previous = new TestRunResult
        {
            Id = "prev",
            Timestamp = DateTime.Now.AddMinutes(-5),
            Passed = 2,
            Failed = 0,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            PassedTests = ["Test1", "Test2"]
        };

        var current = new TestRunResult
        {
            Id = "curr",
            Timestamp = DateTime.Now,
            Passed = 2,
            Failed = 1,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(3),
            PassedTests = ["Test1", "Test2"],
            FailedTests = ["Test3"] // New test, not a regression
        };

        var regressions = current.GetRegressions(previous);

        Assert.Empty(regressions);
    }
}
