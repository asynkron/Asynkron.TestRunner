using System;

namespace Asynkron.TestRunner.Models;

public class TestRunResult
{
    public required string Id { get; set; }
    public required DateTime Timestamp { get; set; }
    public required int Passed { get; set; }
    public required int Failed { get; set; }
    public required int Skipped { get; set; }
    public required TimeSpan Duration { get; set; }
    public string? TrxFilePath { get; set; }

    /// <summary>
    /// Names of tests that failed in this run (assertion failures)
    /// </summary>
    public List<string> FailedTests { get; set; } = [];

    /// <summary>
    /// Names of tests that timed out (hung)
    /// </summary>
    public List<string> TimedOutTests { get; set; } = [];

    /// <summary>
    /// Names of tests that passed in this run
    /// </summary>
    public List<string> PassedTests { get; set; } = [];

    /// <summary>
    /// Completion order for the run (used for heatmap rendering).
    /// </summary>
    public List<SlotStatus> CompletionOrder { get; set; } = [];

    public int Total => Passed + Failed + Skipped;
    public double PassRate => Total > 0 ? (double)Passed / Total * 100 : 0;


    /// <summary>
    /// Find tests that regressed (passed before, fail now)
    /// </summary>
    public List<string> GetRegressions(TestRunResult? previousRun)
    {
        if (previousRun == null)
        {
            return [];
        }

        var previousPassed = new HashSet<string>(previousRun.PassedTests);
        return FailedTests.Where(previousPassed.Contains).ToList();
    }

    /// <summary>
    /// Find tests that were fixed (failed before, pass now)
    /// </summary>
    public List<string> GetFixes(TestRunResult? previousRun)
    {
        if (previousRun == null)
        {
            return [];
        }

        var previousFailed = new HashSet<string>(previousRun.FailedTests);
        return PassedTests.Where(previousFailed.Contains).ToList();
    }

    public static List<string> GetFlakyTests(IReadOnlyList<TestRunResult> runs)
    {
        if (runs.Count < 2)
        {
            return [];
        }

        var statusByTest = new Dictionary<string, HashSet<string>>();

        void AddStatus(IEnumerable<string> tests, string status)
        {
            foreach (var test in tests)
            {
                if (!statusByTest.TryGetValue(test, out var statuses))
                {
                    statuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    statusByTest[test] = statuses;
                }

                statuses.Add(status);
            }
        }

        foreach (var run in runs)
        {
            AddStatus(run.PassedTests, "passed");
            AddStatus(run.FailedTests, "failed");
            AddStatus(run.TimedOutTests, "timedout");
        }

        return statusByTest
            .Where(kvp => kvp.Value.Count > 1)
            .Select(kvp => kvp.Key)
            .OrderBy(test => test)
            .ToList();
    }
}
