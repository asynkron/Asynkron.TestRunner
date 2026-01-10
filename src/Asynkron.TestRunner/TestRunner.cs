using System.Diagnostics;
using System.Text;
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

/// <summary>
/// Detailed result for a single test
/// </summary>
public record TestResultDetail(
    string FullyQualifiedName,
    string DisplayName,
    string Status,  // "passed", "failed", "skipped", "crashed", "hanging"
    double DurationMs,
    string? ErrorMessage = null,
    string? StackTrace = null,
    string? Output = null,
    string? SkipReason = null
);

public class TestRunner
{
    private readonly ResultStore _store;
    private readonly int _testTimeoutSeconds;
    private readonly string? _filter;
    private readonly bool _quiet;
    private readonly int _workerCount;
    private readonly bool _verbose;
    private readonly string? _logFile;
    private readonly object _logLock = new();
    private readonly Action<TestResultDetail>? _resultCallback;

    public TestRunner(ResultStore store, int? timeoutSeconds = null, string? filter = null, bool quiet = false, int workerCount = 1, bool verbose = false, string? logFile = null, Action<TestResultDetail>? resultCallback = null)
    {
        _store = store;
        _testTimeoutSeconds = timeoutSeconds ?? 30;
        _filter = filter;
        _quiet = quiet;
        _workerCount = Math.Max(1, workerCount);
        _verbose = verbose;
        _logFile = logFile;
        _resultCallback = resultCallback;
    }

    private void Log(int workerIndex, string message)
    {
        if (!_verbose && _logFile == null) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] Worker {workerIndex}: {message}";

        lock (_logLock)
        {
            if (_logFile != null)
                File.AppendAllText(_logFile, line + Environment.NewLine);
            if (_verbose)
                Console.Error.WriteLine(line);
        }
    }

    private void ReportCrashedOrHanging(string fqn, string status, Dictionary<string, StringBuilder>? testOutput = null, string? errorMessage = null)
    {
        _resultCallback?.Invoke(new TestResultDetail(
            fqn,
            fqn, // Use FQN as display name for crashed/hanging
            status,
            0,
            errorMessage,
            Output: testOutput?.TryGetValue(fqn, out var output) == true ? output.ToString() : null
        ));
        testOutput?.Remove(fqn);
    }

    public async Task<int> RunTestsAsync(string[] assemblyPaths, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var results = new TestResults();

        foreach (var assemblyPath in assemblyPaths)
        {
            ct.ThrowIfCancellationRequested();

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
                await RunWithRecoveryQuietAsync(assemblyPath, allTests, results, ct);
            }
            else
            {
                await RunWithRecoveryLiveAsync(assemblyPath, allTests, results, ct);
            }
        }

        stopwatch.Stop();
        PrintSummary(results, stopwatch.Elapsed);
        SaveResults(results, stopwatch.Elapsed);

        return results.Failed.Count > 0 || results.Crashed.Count > 0 || results.Hanging.Count > 0 ? 1 : 0;
    }

    private async Task RunWithRecoveryLiveAsync(string assemblyPath, List<string> allTests, TestResults results, CancellationToken ct)
    {
        var display = new LiveDisplay();
        display.SetTotal(allTests.Count);
        display.SetFilter(_filter);
        display.SetAssembly(assemblyPath);
        display.SetWorkerCount(_workerCount);
        display.SetTimeout(_testTimeoutSeconds);

        var failureDetails = new List<(string Name, string Message, string? Stack)>();
        var failureLock = new object();

        // Split tests into batches for each worker
        var batches = SplitIntoBatches(allTests, _workerCount);

        // Set worker batch info for per-worker progress bars
        var offset = 0;
        for (var i = 0; i < batches.Count; i++)
        {
            display.SetWorkerBatch(i, offset, batches[i].Count);
            offset += batches[i].Count;
        }

        await AnsiConsole.Live(display.Render())
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                // Create update callback that refreshes the display
                void UpdateDisplay() => ctx.UpdateTarget(display.Render());

                // Run all workers in parallel
                var workerTasks = batches.Select((batch, index) =>
                    RunWorkerBatchAsync(assemblyPath, batch, index, results, display, failureDetails, failureLock, UpdateDisplay, ct)
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
        // Chunk-based: consecutive tests stay together (siblings in same worker)
        var batches = new List<List<string>>();
        var chunkSize = (tests.Count + batchCount - 1) / batchCount; // Ceiling division

        for (var i = 0; i < batchCount; i++)
        {
            var start = i * chunkSize;
            var count = Math.Min(chunkSize, tests.Count - start);
            batches.Add(count > 0 ? tests.GetRange(start, count) : []);
        }

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
        Action updateDisplay,
        CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            display.WorkerComplete(workerIndex);
            return;
        }

        var pending = new HashSet<string>(batch);
        var running = new HashSet<string>();
        var suspectedCrashTests = new HashSet<string>(); // Tests that need isolated retry
        var testOutput = new Dictionary<string, StringBuilder>(); // Capture output per test
        var testStartTimes = new Dictionary<string, DateTime>();

        Log(workerIndex, $"Starting batch: {batch.Count} tests");

        while (pending.Count > 0 || suspectedCrashTests.Count > 0)
        {
            if (ct.IsCancellationRequested)
            {
                display.WorkerComplete(workerIndex);
                return;
            }

            running.Clear();

            // If we have suspected crash tests, run them one at a time
            List<string> testsToRun;
            var isolatedMode = false;
            if (suspectedCrashTests.Count > 0)
            {
                testsToRun = [suspectedCrashTests.First()];
                isolatedMode = true;
                Log(workerIndex, $"ISOLATED MODE: testing '{testsToRun[0]}' (suspected: {suspectedCrashTests.Count}, pending: {pending.Count})");
            }
            else
            {
                testsToRun = pending.ToList();
                Log(workerIndex, $"Running batch of {testsToRun.Count} tests");
            }

            await using var worker = WorkerProcess.Spawn();

            try
            {
                using var cts = new CancellationTokenSource();
                var pendingBeforeRun = pending.Count;

                await foreach (var msg in worker.RunAsync(assemblyPath, testsToRun, _testTimeoutSeconds, cts.Token)
                    .WithTimeout(TimeSpan.FromSeconds(_testTimeoutSeconds), cts))
                {
                    display.WorkerActivity(workerIndex);

                    switch (msg)
                    {
                        case TestStartedEvent started:
                            running.Add(started.FullyQualifiedName);
                            testStartTimes[started.FullyQualifiedName] = DateTime.UtcNow;
                            display.TestStarted(started.DisplayName);
                            break;

                        case TestOutputEvent output:
                            if (!testOutput.TryGetValue(output.FullyQualifiedName, out var sb))
                            {
                                sb = new StringBuilder();
                                testOutput[output.FullyQualifiedName] = sb;
                            }
                            sb.AppendLine(output.Text);
                            break;

                        case TestPassedEvent testPassed:
                            running.Remove(testPassed.FullyQualifiedName);
                            pending.Remove(testPassed.FullyQualifiedName);
                            suspectedCrashTests.Remove(testPassed.FullyQualifiedName);
                            lock (results) results.Passed.Add(testPassed.FullyQualifiedName);
                            display.TestPassed(testPassed.DisplayName);
                            display.WorkerTestPassed(workerIndex);
                            _resultCallback?.Invoke(new TestResultDetail(
                                testPassed.FullyQualifiedName,
                                testPassed.DisplayName,
                                "passed",
                                testPassed.DurationMs,
                                Output: testOutput.TryGetValue(testPassed.FullyQualifiedName, out var passedOutput) ? passedOutput.ToString() : null
                            ));
                            testOutput.Remove(testPassed.FullyQualifiedName);
                            break;

                        case TestFailedEvent testFailed:
                            running.Remove(testFailed.FullyQualifiedName);
                            pending.Remove(testFailed.FullyQualifiedName);
                            suspectedCrashTests.Remove(testFailed.FullyQualifiedName);
                            lock (results) results.Failed.Add(testFailed.FullyQualifiedName);
                            display.TestFailed(testFailed.DisplayName);
                            display.WorkerTestFailed(workerIndex);
                            lock (failureLock) failureDetails.Add((testFailed.DisplayName, testFailed.ErrorMessage, testFailed.StackTrace));
                            _resultCallback?.Invoke(new TestResultDetail(
                                testFailed.FullyQualifiedName,
                                testFailed.DisplayName,
                                "failed",
                                testFailed.DurationMs,
                                testFailed.ErrorMessage,
                                testFailed.StackTrace,
                                testOutput.TryGetValue(testFailed.FullyQualifiedName, out var failedOutput) ? failedOutput.ToString() : null
                            ));
                            testOutput.Remove(testFailed.FullyQualifiedName);
                            break;

                        case TestSkippedEvent testSkipped:
                            running.Remove(testSkipped.FullyQualifiedName);
                            pending.Remove(testSkipped.FullyQualifiedName);
                            suspectedCrashTests.Remove(testSkipped.FullyQualifiedName);
                            lock (results) results.Skipped.Add(testSkipped.FullyQualifiedName);
                            display.TestSkipped(testSkipped.DisplayName);
                            display.WorkerTestPassed(workerIndex); // Skipped counts as "ok" for heat map
                            _resultCallback?.Invoke(new TestResultDetail(
                                testSkipped.FullyQualifiedName,
                                testSkipped.DisplayName,
                                "skipped",
                                0,
                                SkipReason: testSkipped.Reason
                            ));
                            break;

                        case RunCompletedEvent:
                            foreach (var fqn in running.ToList())
                            {
                                running.Remove(fqn);
                                pending.Remove(fqn);
                                lock (results) results.Crashed.Add(fqn);
                                display.TestCrashed(fqn);
                                display.WorkerTestCrashed(workerIndex);
                                ReportCrashedOrHanging(fqn, "crashed", testOutput, "Test did not report completion");
                            }
                            break;

                        case ErrorEvent:
                            break;
                    }
                }

                // Fallback: if stream ended but tests still running, mark as crashed
                foreach (var fqn in running.ToList())
                {
                    running.Remove(fqn);
                    pending.Remove(fqn);
                    lock (results) results.Crashed.Add(fqn);
                    display.TestCrashed(fqn);
                    display.WorkerTestCrashed(workerIndex);
                    ReportCrashedOrHanging(fqn, "crashed", testOutput, "Stream ended while test was running");
                }

                // If no progress was made (tests never started), blame the first test and retry the rest
                if (pending.Count > 0 && pending.Count == pendingBeforeRun)
                {
                    var firstTest = pending.First();
                    pending.Remove(firstTest);
                    lock (results) results.Crashed.Add(firstTest);
                    display.TestCrashed(firstTest);
                    display.WorkerTestCrashed(workerIndex);
                    display.WorkerRestarting(workerIndex);
                    ReportCrashedOrHanging(firstTest, "crashed", testOutput, "Test prevented batch from starting");
                }
            }
            catch (TimeoutException)
            {
                Log(workerIndex, $"TIMEOUT: running={running.Count}, isolated={isolatedMode}, testsToRun={testsToRun.Count}");
                if (isolatedMode || running.Count <= 1)
                {
                    // Running single test - it's definitely hanging
                    // Mark either the running test or the test we tried to run
                    var testsToMark = running.Count > 0 ? running.ToList() : testsToRun;
                    foreach (var fqn in testsToMark)
                    {
                        Log(workerIndex, $"HANGING (confirmed): {fqn}");
                        pending.Remove(fqn);
                        suspectedCrashTests.Remove(fqn);
                        lock (results) results.Hanging.Add(fqn);
                        display.TestHanging(fqn);
                        display.WorkerTestHanging(workerIndex);
                        ReportCrashedOrHanging(fqn, "hanging", testOutput, $"Test exceeded timeout of {_testTimeoutSeconds}s");
                    }
                }
                else
                {
                    // Multiple tests running - can't tell which one is hanging
                    // Move them to suspected list for isolated retry
                    Log(workerIndex, $"Moving {running.Count} tests to suspected list");
                    foreach (var fqn in running)
                    {
                        pending.Remove(fqn);
                        suspectedCrashTests.Add(fqn);
                    }
                }
                worker.Kill();
                display.WorkerRestarting(workerIndex);
            }
            catch (WorkerCrashedException ex)
            {
                Log(workerIndex, $"CRASHED: running={running.Count}, isolated={isolatedMode}, exit={ex.ExitCode}, testsToRun={testsToRun.Count}");
                if (isolatedMode || running.Count <= 1)
                {
                    // Running single test - it's definitely the culprit
                    // Mark either the running test or the test we tried to run
                    var testsToMark = running.Count > 0 ? running.ToList() : testsToRun;
                    foreach (var fqn in testsToMark)
                    {
                        Log(workerIndex, $"CRASHED (confirmed): {fqn}");
                        pending.Remove(fqn);
                        suspectedCrashTests.Remove(fqn);
                        lock (results) results.Crashed.Add(fqn);
                        display.TestCrashed(fqn);
                        display.WorkerTestCrashed(workerIndex);
                        ReportCrashedOrHanging(fqn, "crashed", testOutput, $"Worker crashed with exit code {ex.ExitCode}");
                    }
                }
                else
                {
                    // Multiple tests running - can't tell which one crashed
                    // Move them to suspected list for isolated retry
                    Log(workerIndex, $"Moving {running.Count} tests to suspected list (crash)");
                    foreach (var fqn in running)
                    {
                        pending.Remove(fqn);
                        suspectedCrashTests.Add(fqn);
                    }
                }
                display.WorkerRestarting(workerIndex);
            }
            catch (Exception ex)
            {
                // Treat any other exception like a crash - isolate and retry
                Log(workerIndex, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}, running={running.Count}, isolated={isolatedMode}");
                if (isolatedMode || running.Count <= 1)
                {
                    var testsToMark = running.Count > 0 ? running.ToList() : testsToRun;
                    foreach (var fqn in testsToMark)
                    {
                        Log(workerIndex, $"CRASHED (exception): {fqn}");
                        pending.Remove(fqn);
                        suspectedCrashTests.Remove(fqn);
                        lock (results) results.Crashed.Add(fqn);
                        display.TestCrashed(fqn);
                        display.WorkerTestCrashed(workerIndex);
                        ReportCrashedOrHanging(fqn, "crashed", testOutput, $"{ex.GetType().Name}: {ex.Message}");
                    }
                }
                else
                {
                    Log(workerIndex, $"Moving {running.Count} tests to suspected list (exception)");
                    foreach (var fqn in running)
                    {
                        pending.Remove(fqn);
                        suspectedCrashTests.Add(fqn);
                    }
                }
                worker.Kill();
                display.WorkerRestarting(workerIndex);
            }
        }

        // After all retries, mark any remaining tests as crashed
        var remaining = pending.Concat(suspectedCrashTests).ToList();
        if (remaining.Count > 0)
        {
            Log(workerIndex, $"GIVING UP on {remaining.Count} tests");
            foreach (var fqn in remaining)
            {
                Log(workerIndex, $"ABANDONED: {fqn}");
                lock (results) results.Crashed.Add(fqn);
                display.TestCrashed(fqn);
                display.WorkerTestCrashed(workerIndex);
                ReportCrashedOrHanging(fqn, "crashed", testOutput, "Test abandoned after max retries");
            }
        }

        Log(workerIndex, "COMPLETE");
        display.WorkerComplete(workerIndex);
    }

    private async Task RunWithRecoveryQuietAsync(string assemblyPath, List<string> allTests, TestResults results, CancellationToken ct)
    {
        var pending = new HashSet<string>(allTests);
        var running = new HashSet<string>();
        var attempts = 0;
        const int maxAttempts = 100;

        while (pending.Count > 0 && attempts < maxAttempts)
        {
            if (ct.IsCancellationRequested)
                return;
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
