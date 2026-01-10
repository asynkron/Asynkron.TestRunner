using System.Diagnostics;
using Asynkron.TestRunner.Models;
using Asynkron.TestRunner.Protocol;
using Spectre.Console;

namespace Asynkron.TestRunner;

public enum TestState
{
    Pending,
    Running,
    Passed,
    Failed,
    Skipped,
    Crashed,   // Worker died while test was running
    Hanging    // No output received within timeout
}

public class TestRunner
{
    private readonly ResultStore _store;
    private readonly int _testTimeoutSeconds;
    private readonly string? _filter;
    private readonly bool _quiet;

    public TestRunner(ResultStore store, int? timeoutSeconds = null, string? filter = null, bool quiet = false)
    {
        _store = store;
        _testTimeoutSeconds = timeoutSeconds ?? 30;
        _filter = filter;
        _quiet = quiet;
    }

    public async Task<int> RunTestsAsync(string[] assemblyPaths)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new TestResults();

        foreach (var assemblyPath in assemblyPaths)
        {
            if (!File.Exists(assemblyPath))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Assembly not found: {assemblyPath}");
                continue;
            }

            AnsiConsole.MarkupLine($"[dim]Running tests in:[/] {Path.GetFileName(assemblyPath)}");

            // Discover all tests upfront
            var filter = TestFilter.Parse(_filter);
            var discovered = await TestDiscovery.DiscoverTestsAsync([assemblyPath], filter);
            var allTests = discovered.Select(t => t.FullyQualifiedName).ToList();

            if (allTests.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(_filter))
                    AnsiConsole.MarkupLine($"[yellow]No tests match filter:[/] {_filter}");
                else
                    AnsiConsole.MarkupLine($"[yellow]No tests found[/]");
                continue;
            }

            AnsiConsole.MarkupLine($"[dim]Found {allTests.Count} tests[/]");

            // Run with resilient recovery
            await RunWithRecoveryAsync(assemblyPath, allTests, results);
        }

        stopwatch.Stop();
        PrintSummary(results, stopwatch.Elapsed);
        SaveResults(results, stopwatch.Elapsed);

        return results.Failed.Count > 0 || results.Crashed.Count > 0 || results.Hanging.Count > 0 ? 1 : 0;
    }

    private async Task RunWithRecoveryAsync(string assemblyPath, List<string> allTests, TestResults results)
    {
        var pending = new HashSet<string>(allTests);
        var running = new HashSet<string>();
        var attempts = 0;
        const int maxAttempts = 100; // Prevent infinite loops

        while (pending.Count > 0 && attempts < maxAttempts)
        {
            attempts++;
            running.Clear();

            await using var worker = WorkerProcess.Spawn();

            try
            {
                using var cts = new CancellationTokenSource();
                var testsToRun = pending.ToList();

                await foreach (var msg in worker.RunAsync(assemblyPath, testsToRun, _testTimeoutSeconds, cts.Token)
                    .WithTimeout(TimeSpan.FromSeconds(_testTimeoutSeconds), cts))
                {
                    switch (msg)
                    {
                        case TestStartedEvent started:
                            running.Add(started.FullyQualifiedName);
                            if (!_quiet)
                                AnsiConsole.Markup($"[dim]  ►[/] {started.DisplayName}");
                            break;

                        case TestPassedEvent testPassed:
                            running.Remove(testPassed.FullyQualifiedName);
                            pending.Remove(testPassed.FullyQualifiedName);
                            results.Passed.Add(testPassed.FullyQualifiedName);
                            if (!_quiet)
                                AnsiConsole.MarkupLine($"\r[green]  ✓[/] {testPassed.DisplayName} [dim]({testPassed.DurationMs:F0}ms)[/]");
                            break;

                        case TestFailedEvent testFailed:
                            running.Remove(testFailed.FullyQualifiedName);
                            pending.Remove(testFailed.FullyQualifiedName);
                            results.Failed.Add(testFailed.FullyQualifiedName);
                            AnsiConsole.MarkupLine($"\r[red]  ✗[/] {testFailed.DisplayName} [dim]({testFailed.DurationMs:F0}ms)[/]");
                            if (!_quiet)
                            {
                                AnsiConsole.MarkupLine($"[red]    {EscapeMarkup(testFailed.ErrorMessage)}[/]");
                                if (testFailed.StackTrace != null)
                                {
                                    var firstLine = testFailed.StackTrace.Split('\n').FirstOrDefault()?.Trim();
                                    if (firstLine != null)
                                        AnsiConsole.MarkupLine($"[dim]    {EscapeMarkup(firstLine)}[/]");
                                }
                            }
                            break;

                        case TestSkippedEvent testSkipped:
                            running.Remove(testSkipped.FullyQualifiedName);
                            pending.Remove(testSkipped.FullyQualifiedName);
                            results.Skipped.Add(testSkipped.FullyQualifiedName);
                            if (!_quiet)
                                AnsiConsole.MarkupLine($"[yellow]  ○[/] {testSkipped.DisplayName} [dim](skipped{(testSkipped.Reason != null ? $": {testSkipped.Reason}" : "")})[/]");
                            break;

                        case TestOutputEvent output:
                            if (!_quiet && !string.IsNullOrWhiteSpace(output.Text))
                                AnsiConsole.MarkupLine($"[dim]    │ {EscapeMarkup(output.Text.TrimEnd())}[/]");
                            break;

                        case RunCompletedEvent:
                            // All done for this worker
                            break;

                        case ErrorEvent error:
                            AnsiConsole.MarkupLine($"[red]Worker error:[/] {error.Message}");
                            if (error.Details != null)
                                AnsiConsole.MarkupLine($"[dim]{error.Details}[/]");
                            break;
                    }
                }
            }
            catch (TimeoutException)
            {
                // Tests in running state are hanging
                foreach (var fqn in running)
                {
                    pending.Remove(fqn);
                    results.Hanging.Add(fqn);
                    AnsiConsole.MarkupLine($"\r[red]  ⏱[/] {fqn} [red](HANGING - no response for {_testTimeoutSeconds}s)[/]");
                }

                worker.Kill();

                if (pending.Count > 0)
                    AnsiConsole.MarkupLine($"[yellow]  Restarting worker to continue with {pending.Count} remaining tests...[/]");
            }
            catch (WorkerCrashedException ex)
            {
                // Tests in running state caused crash
                foreach (var fqn in running)
                {
                    pending.Remove(fqn);
                    results.Crashed.Add(fqn);
                    AnsiConsole.MarkupLine($"\r[red]  💥[/] {fqn} [red](CRASHED - worker died{(ex.ExitCode.HasValue ? $" with exit code {ex.ExitCode}" : "")})[/]");
                }

                if (pending.Count > 0)
                    AnsiConsole.MarkupLine($"[yellow]  Restarting worker to continue with {pending.Count} remaining tests...[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {ex.Message}");
                worker.Kill();
                break;
            }
        }

        if (attempts >= maxAttempts)
        {
            AnsiConsole.MarkupLine($"[red]Stopped after {maxAttempts} attempts - possible infinite crash loop[/]");
        }
    }

    private void PrintSummary(TestResults results, TimeSpan elapsed)
    {
        Console.WriteLine();

        var passColor = results.Passed.Count > 0 ? "green" : "dim";
        var failColor = results.Failed.Count > 0 ? "red" : "dim";
        var skipColor = results.Skipped.Count > 0 ? "yellow" : "dim";
        var hangColor = results.Hanging.Count > 0 ? "red" : "dim";
        var crashColor = results.Crashed.Count > 0 ? "red" : "dim";

        AnsiConsole.MarkupLine(
            $"[{passColor}]{results.Passed.Count} passed[/], " +
            $"[{failColor}]{results.Failed.Count} failed[/], " +
            $"[{skipColor}]{results.Skipped.Count} skipped[/], " +
            $"[{hangColor}]{results.Hanging.Count} hanging[/], " +
            $"[{crashColor}]{results.Crashed.Count} crashed[/] " +
            $"[dim]({elapsed.TotalSeconds:F1}s)[/]");

        // Report problematic tests
        if (results.Hanging.Count > 0)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine($"[red]⏱ Hanging tests ({results.Hanging.Count}):[/]");
            foreach (var test in results.Hanging.Take(10))
                AnsiConsole.MarkupLine($"  [red]→[/] {test}");
            if (results.Hanging.Count > 10)
                AnsiConsole.MarkupLine($"  [dim]...and {results.Hanging.Count - 10} more[/]");
        }

        if (results.Crashed.Count > 0)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine($"[red]💥 Crashed tests ({results.Crashed.Count}):[/]");
            foreach (var test in results.Crashed.Take(10))
                AnsiConsole.MarkupLine($"  [red]→[/] {test}");
            if (results.Crashed.Count > 10)
                AnsiConsole.MarkupLine($"  [dim]...and {results.Crashed.Count - 10} more[/]");
        }
    }

    private void SaveResults(TestResults results, TimeSpan elapsed)
    {
        var result = new TestRunResult
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = DateTime.UtcNow,
            Passed = results.Passed.Count,
            Failed = results.Failed.Count,
            Skipped = results.Skipped.Count,
            Duration = elapsed,
            PassedTests = results.Passed.ToList(),
            FailedTests = results.Failed.ToList(),
            TimedOutTests = results.Hanging.Concat(results.Crashed).ToList()
        };

        _store.SaveResult(result);

        // Check for regressions
        var recentRuns = _store.GetRecentRuns(2);
        if (recentRuns.Count >= 2)
        {
            var previous = recentRuns[1];
            var regressions = result.GetRegressions(previous);
            if (regressions.Count > 0)
            {
                Console.WriteLine();
                AnsiConsole.MarkupLine($"[red]⚠ {regressions.Count} regression(s) detected![/]");
                foreach (var reg in regressions.Take(5))
                    AnsiConsole.MarkupLine($"  [red]→[/] {reg}");
                if (regressions.Count > 5)
                    AnsiConsole.MarkupLine($"  [dim]...and {regressions.Count - 5} more[/]");
            }

            var fixes = result.GetFixes(previous);
            if (fixes.Count > 0)
            {
                AnsiConsole.MarkupLine($"[green]✓ {fixes.Count} fix(es)![/]");
            }
        }
    }

    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}

public class TestResults
{
    public HashSet<string> Passed { get; } = new();
    public HashSet<string> Failed { get; } = new();
    public HashSet<string> Skipped { get; } = new();
    public HashSet<string> Hanging { get; } = new();
    public HashSet<string> Crashed { get; } = new();
}

public class WorkerCrashedException : Exception
{
    public int? ExitCode { get; }

    public WorkerCrashedException(int? exitCode)
        : base($"Worker process crashed{(exitCode.HasValue ? $" with exit code {exitCode}" : "")}")
    {
        ExitCode = exitCode;
    }
}

public static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> WithTimeout<T>(
        this IAsyncEnumerable<T> source,
        TimeSpan timeout,
        CancellationTokenSource cts)
    {
        var enumerator = source.GetAsyncEnumerator(cts.Token);

        try
        {
            while (true)
            {
                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);

                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cts.Token.IsCancellationRequested)
                {
                    throw new TimeoutException($"No message received within {timeout.TotalSeconds}s");
                }

                if (!hasNext)
                    yield break;

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
