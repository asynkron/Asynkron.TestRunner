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
    private readonly int _workerCount;

    public TestRunner(ResultStore store, int? timeoutSeconds = null, string? filter = null, bool quiet = false, int workerCount = 1)
    {
        _store = store;
        _testTimeoutSeconds = timeoutSeconds ?? 30;
        _filter = filter;
        _quiet = quiet;
        _workerCount = Math.Max(1, workerCount);
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

            // Discover tests using worker (NUnit.Engine expands parameterized tests correctly)
            AnsiConsole.MarkupLine($"[dim]Discovering tests...[/]");
            List<string> allTests;
            await using (var discoveryWorker = WorkerProcess.Spawn())
            {
                var discovered = await discoveryWorker.DiscoverAsync(assemblyPath);
                var totalDiscovered = discovered.Count;

                // Apply filter if specified
                if (!string.IsNullOrWhiteSpace(_filter))
                {
                    var filter = TestFilter.Parse(_filter);
                    discovered = discovered.Where(t => filter.Matches(t.FullyQualifiedName, t.DisplayName)).ToList();
                    var skippedCount = discovered.Count(t => t.SkipReason != null);
                    AnsiConsole.MarkupLine($"[dim]Filter applied: {discovered.Count}/{totalDiscovered} tests match ({skippedCount} skipped)[/]");
                }

                allTests = discovered.Select(t => t.FullyQualifiedName).ToList();
            }

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
            if (_quiet)
            {
                await RunWithRecoveryQuietAsync(assemblyPath, allTests, results);
            }
            else
            {
                await RunWithRecoveryLiveAsync(assemblyPath, allTests, results);
            }
        }

        stopwatch.Stop();
        PrintSummary(results, stopwatch.Elapsed);
        SaveResults(results, stopwatch.Elapsed);

        return results.Failed.Count > 0 || results.Crashed.Count > 0 || results.Hanging.Count > 0 ? 1 : 0;
    }

    private async Task RunWithRecoveryLiveAsync(string assemblyPath, List<string> allTests, TestResults results)
    {
        var display = new LiveDisplay();
        display.SetTotal(allTests.Count);
        display.SetFilter(_filter);
        display.SetAssembly(assemblyPath);
        display.SetWorkerCount(_workerCount);

        var failureDetails = new List<(string Name, string Message, string? Stack)>();
        var failureLock = new object();

        // Split tests into batches for each worker
        var batches = SplitIntoBatches(allTests, _workerCount);

        await AnsiConsole.Live(display.Render())
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                // Create update callback that refreshes the display
                void UpdateDisplay() => ctx.UpdateTarget(display.Render());

                // Run all workers in parallel
                var workerTasks = batches.Select((batch, index) =>
                    RunWorkerBatchAsync(assemblyPath, batch, index, results, display, failureDetails, failureLock, UpdateDisplay)
                ).ToArray();

                // Periodically refresh display while workers are running
                var allDone = Task.WhenAll(workerTasks);
                while (!allDone.IsCompleted)
                {
                    UpdateDisplay();
                    await Task.WhenAny(allDone, Task.Delay(100));
                }

                await allDone;
                UpdateDisplay();
            });

        // Print failure details after live display
        if (failureDetails.Count > 0)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine($"[red]Failed tests ({failureDetails.Count}):[/]");
            foreach (var (name, message, stack) in failureDetails.Take(10))
            {
                AnsiConsole.MarkupLine($"[red]  ✗[/] {EscapeMarkup(name)}");
                AnsiConsole.MarkupLine($"[red]    {EscapeMarkup(message)}[/]");
                if (stack != null)
                {
                    var firstLine = stack.Split('\n').FirstOrDefault()?.Trim();
                    if (firstLine != null)
                        AnsiConsole.MarkupLine($"[dim]    {EscapeMarkup(firstLine)}[/]");
                }
            }
            if (failureDetails.Count > 10)
                AnsiConsole.MarkupLine($"[dim]  ...and {failureDetails.Count - 10} more[/]");
        }
    }

    private static List<List<string>> SplitIntoBatches(List<string> tests, int batchCount)
    {
        var batches = new List<List<string>>();
        for (var i = 0; i < batchCount; i++)
            batches.Add(new List<string>());

        // Round-robin distribution for balanced batches
        for (var i = 0; i < tests.Count; i++)
            batches[i % batchCount].Add(tests[i]);

        return batches;
    }

    private async Task RunWorkerBatchAsync(
        string assemblyPath,
        List<string> batch,
        int workerIndex,
        TestResults results,
        LiveDisplay display,
        List<(string Name, string Message, string? Stack)> failureDetails,
        object failureLock,
        Action updateDisplay)
    {
        if (batch.Count == 0) return;

        var pending = new HashSet<string>(batch);
        var running = new HashSet<string>();
        var attempts = 0;
        const int maxAttempts = 100;

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
                            display.TestStarted(started.DisplayName);
                            break;

                        case TestPassedEvent testPassed:
                            running.Remove(testPassed.FullyQualifiedName);
                            pending.Remove(testPassed.FullyQualifiedName);
                            lock (results) results.Passed.Add(testPassed.FullyQualifiedName);
                            display.TestPassed(testPassed.DisplayName);
                            break;

                        case TestFailedEvent testFailed:
                            running.Remove(testFailed.FullyQualifiedName);
                            pending.Remove(testFailed.FullyQualifiedName);
                            lock (results) results.Failed.Add(testFailed.FullyQualifiedName);
                            display.TestFailed(testFailed.DisplayName);
                            lock (failureLock) failureDetails.Add((testFailed.DisplayName, testFailed.ErrorMessage, testFailed.StackTrace));
                            break;

                        case TestSkippedEvent testSkipped:
                            running.Remove(testSkipped.FullyQualifiedName);
                            pending.Remove(testSkipped.FullyQualifiedName);
                            lock (results) results.Skipped.Add(testSkipped.FullyQualifiedName);
                            display.TestSkipped(testSkipped.DisplayName);
                            break;

                        case RunCompletedEvent:
                            foreach (var fqn in running.ToList())
                            {
                                running.Remove(fqn);
                                pending.Remove(fqn);
                                lock (results) results.Crashed.Add(fqn);
                                display.TestCrashed(fqn);
                            }
                            break;

                        case ErrorEvent:
                            break;
                    }
                }
            }
            catch (TimeoutException)
            {
                foreach (var fqn in running)
                {
                    pending.Remove(fqn);
                    lock (results) results.Hanging.Add(fqn);
                    display.TestHanging(fqn);
                }
                worker.Kill();
                display.WorkerRestarted(pending.Count);
            }
            catch (WorkerCrashedException)
            {
                foreach (var fqn in running)
                {
                    pending.Remove(fqn);
                    lock (results) results.Crashed.Add(fqn);
                    display.TestCrashed(fqn);
                }
                display.WorkerRestarted(pending.Count);
            }
            catch (Exception)
            {
                worker.Kill();
                break;
            }
        }
    }

    private async Task RunWithRecoveryQuietAsync(string assemblyPath, List<string> allTests, TestResults results)
    {
        var pending = new HashSet<string>(allTests);
        var running = new HashSet<string>();
        var attempts = 0;
        const int maxAttempts = 100;

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
                            break;

                        case TestPassedEvent testPassed:
                            running.Remove(testPassed.FullyQualifiedName);
                            pending.Remove(testPassed.FullyQualifiedName);
                            results.Passed.Add(testPassed.FullyQualifiedName);
                            break;

                        case TestFailedEvent testFailed:
                            running.Remove(testFailed.FullyQualifiedName);
                            pending.Remove(testFailed.FullyQualifiedName);
                            results.Failed.Add(testFailed.FullyQualifiedName);
                            AnsiConsole.MarkupLine($"[red]  ✗[/] {EscapeMarkup(testFailed.DisplayName)}");
                            break;

                        case TestSkippedEvent testSkipped:
                            running.Remove(testSkipped.FullyQualifiedName);
                            pending.Remove(testSkipped.FullyQualifiedName);
                            results.Skipped.Add(testSkipped.FullyQualifiedName);
                            break;

                        case RunCompletedEvent:
                            // Mark any still-running tests as crashed
                            foreach (var fqn in running.ToList())
                            {
                                running.Remove(fqn);
                                pending.Remove(fqn);
                                results.Crashed.Add(fqn);
                                AnsiConsole.MarkupLine($"[red]  💥[/] {fqn} [red](NO RESULT)[/]");
                            }
                            break;

                        case ErrorEvent:
                            break;
                    }
                }
            }
            catch (TimeoutException)
            {
                foreach (var fqn in running)
                {
                    pending.Remove(fqn);
                    results.Hanging.Add(fqn);
                    AnsiConsole.MarkupLine($"[red]  ⏱[/] {fqn} [red](HANGING)[/]");
                }
                worker.Kill();
            }
            catch (WorkerCrashedException ex)
            {
                foreach (var fqn in running)
                {
                    pending.Remove(fqn);
                    results.Crashed.Add(fqn);
                    AnsiConsole.MarkupLine($"[red]  💥[/] {fqn} [red](CRASHED: {ex.ExitCode})[/]");
                }
            }
            catch (Exception)
            {
                worker.Kill();
                break;
            }
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
