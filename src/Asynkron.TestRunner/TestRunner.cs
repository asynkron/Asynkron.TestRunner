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
    private readonly int _hangTimeoutSeconds;
    private readonly string? _filter;
    private readonly bool _quiet;
    private readonly int _workerCount;
    private readonly bool _verbose;
    private readonly string? _logFile;
    private readonly object _logLock = new();
    private readonly Action<TestResultDetail>? _resultCallback;

    public TestRunner(ResultStore store, int? timeoutSeconds = null, int? hangTimeoutSeconds = null, string? filter = null, bool quiet = false, int workerCount = 1, bool verbose = false, string? logFile = null, Action<TestResultDetail>? resultCallback = null)
    {
        _store = store;
        _testTimeoutSeconds = timeoutSeconds ?? 30;
        _hangTimeoutSeconds = hangTimeoutSeconds ?? _testTimeoutSeconds; // Default to same as test timeout
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

        // Shared work queue - workers pull from this
        var queue = new WorkQueue(allTests);
        var initialBatchSize = Math.Max(50, allTests.Count / _workerCount / 4);
        var batchSizeHolder = new BatchSizeHolder(initialBatchSize);
        var tier = 1;

        await AnsiConsole.Live(display.Render())
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                void UpdateDisplay() => ctx.UpdateTarget(display.Render());

                // Run all workers in parallel - they pull batches from shared queue
                var workerTasks = Enumerable.Range(0, _workerCount)
                    .Select(index => RunWorkerAsync(assemblyPath, index, queue, batchSizeHolder, results, display, failureDetails, failureLock, UpdateDisplay, ct))
                    .ToList();

                // Monitor loop: refresh display and handle tier promotion
                while (true)
                {
                    display.SetQueueStats(queue.SuspiciousCount, batchSizeHolder.Size);
                    UpdateDisplay();

                    // Check if we need to promote suspicious tests (tier escalation)
                    if (!queue.HasPendingWork && queue.SuspiciousCount > 0 && queue.AllWorkersIdle)
                    {
                        var promoted = queue.PromoteSuspicious();
                        // Large batches: halve to clear good tests quickly
                        // Small batches (<50): drop aggressively to pinpoint problems
                        var current = batchSizeHolder.Size;
                        var newBatchSize = current switch
                        {
                            > 50 => current / 2,                // Large: halve (5000→2500→1250→...→50)
                            > 10 => 5,                          // Medium: drop to 5
                            > 1 => Math.Max(1, current / 2),    // Small: halve (5→2→1)
                            _ => 1
                        };
                        batchSizeHolder.Size = newBatchSize;
                        tier++;
                        Log(-1, $"TIER {tier}: Promoted {promoted} suspicious tests, batch size {current}→{newBatchSize}");
                    }

                    // Check if all work is done
                    if (queue.IsComplete)
                        break;

                    await Task.Delay(100, ct);
                }

                // Wait for workers to finish
                await Task.WhenAll(workerTasks);
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

    /// <summary>
    /// Holder for mutable batch size shared between workers and monitor
    /// </summary>
    private class BatchSizeHolder
    {
        public int Size;
        public BatchSizeHolder(int initial) => Size = initial;
    }

    /// <summary>
    /// Worker loop: pulls batches from shared queue until no work remains
    /// </summary>
    private async Task RunWorkerAsync(
        string assemblyPath,
        int workerIndex,
        WorkQueue queue,
        BatchSizeHolder batchSizeHolder,
        TestResults results,
        LiveDisplay display,
        List<(string Name, string Message, string? Stack)> failureDetails,
        object failureLock,
        Action updateDisplay,
        CancellationToken ct)
    {
        var running = new HashSet<string>();
        var testOutput = new Dictionary<string, StringBuilder>();
        var testStartTimes = new Dictionary<string, DateTime>();
        var fqnToDisplayName = new Dictionary<string, string>();

        Log(workerIndex, "Worker started");

        while (!ct.IsCancellationRequested)
        {
            // Pull next batch from queue with current batch size
            var currentBatchSize = batchSizeHolder.Size;
            var testsToRun = queue.TakeBatch(workerIndex, currentBatchSize);

            if (testsToRun.Count == 0)
            {
                // No work - check if truly done
                if (queue.IsComplete)
                {
                    Log(workerIndex, "No more work, exiting");
                    break;
                }
                // Wait for promotion or new work
                await Task.Delay(100, ct);
                continue;
            }

            Log(workerIndex, $"Batch of {testsToRun.Count} tests (batch size: {currentBatchSize})");

            running.Clear();

            await using var worker = WorkerProcess.Spawn();

            try
            {
                using var cts = new CancellationTokenSource();
                var completedInBatch = 0;
                // Use hang timeout for small batches (retrying suspect tests)
                var streamTimeout = currentBatchSize <= 10 ? _hangTimeoutSeconds : _testTimeoutSeconds;

                await foreach (var msg in worker.RunAsync(assemblyPath, testsToRun, streamTimeout, cts.Token)
                    .WithTimeout(TimeSpan.FromSeconds(streamTimeout), cts))
                {
                    display.WorkerActivity(workerIndex);

                    // Check for per-test timeout - uses hang timeout for faster detection
                    var now = DateTime.UtcNow;
                    var stuckTests = running
                        .Where(fqn => testStartTimes.TryGetValue(fqn, out var start) &&
                                      (now - start).TotalSeconds >= _hangTimeoutSeconds * 2)
                        .ToList();

                    if (stuckTests.Count > 0)
                    {
                        var suspiciousTests = running
                            .Where(fqn => !stuckTests.Contains(fqn) &&
                                          testStartTimes.TryGetValue(fqn, out var start) &&
                                          (now - start).TotalSeconds >= _hangTimeoutSeconds * 0.75)
                            .ToList();

                        Log(workerIndex, $"STUCK: {stuckTests.Count} exceeded timeout, {suspiciousTests.Count} suspicious");

                        foreach (var fqn in stuckTests)
                        {
                            Log(workerIndex, $"HANGING: {fqn}");
                            running.Remove(fqn);
                            queue.TestHanging(workerIndex, fqn);
                            lock (results) results.Hanging.Add(fqn);
                            var displayName = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                            display.TestHanging(displayName);
                            ReportCrashedOrHanging(fqn, "hanging", testOutput, $"Test exceeded {_hangTimeoutSeconds * 2}s");
                        }

                        queue.MarkSuspicious(workerIndex, suspiciousTests);
                        foreach (var fqn in suspiciousTests)
                        {
                            running.Remove(fqn);
                            var displayName = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                            display.TestRemoved(displayName);
                        }

                        worker.Kill();
                        display.WorkerRestarting(workerIndex);
                        break;
                    }

                    switch (msg)
                    {
                        case TestStartedEvent started:
                            running.Add(started.FullyQualifiedName);
                            testStartTimes[started.FullyQualifiedName] = DateTime.UtcNow;
                            fqnToDisplayName[started.FullyQualifiedName] = started.DisplayName;
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
                            queue.TestCompleted(workerIndex, testPassed.FullyQualifiedName);
                            completedInBatch++;
                            lock (results) results.Passed.Add(testPassed.FullyQualifiedName);
                            display.TestPassed(testPassed.DisplayName);
                            _resultCallback?.Invoke(new TestResultDetail(
                                testPassed.FullyQualifiedName,
                                testPassed.DisplayName,
                                "passed",
                                testPassed.DurationMs,
                                Output: testOutput.GetValueOrDefault(testPassed.FullyQualifiedName)?.ToString()
                            ));
                            testOutput.Remove(testPassed.FullyQualifiedName);
                            break;

                        case TestFailedEvent testFailed:
                            running.Remove(testFailed.FullyQualifiedName);
                            queue.TestCompleted(workerIndex, testFailed.FullyQualifiedName);
                            completedInBatch++;
                            lock (results) results.Failed.Add(testFailed.FullyQualifiedName);
                            display.TestFailed(testFailed.DisplayName);
                            lock (failureLock) failureDetails.Add((testFailed.DisplayName, testFailed.ErrorMessage, testFailed.StackTrace));
                            _resultCallback?.Invoke(new TestResultDetail(
                                testFailed.FullyQualifiedName,
                                testFailed.DisplayName,
                                "failed",
                                testFailed.DurationMs,
                                testFailed.ErrorMessage,
                                testFailed.StackTrace,
                                testOutput.GetValueOrDefault(testFailed.FullyQualifiedName)?.ToString()
                            ));
                            testOutput.Remove(testFailed.FullyQualifiedName);
                            break;

                        case TestSkippedEvent testSkipped:
                            running.Remove(testSkipped.FullyQualifiedName);
                            queue.TestCompleted(workerIndex, testSkipped.FullyQualifiedName);
                            completedInBatch++;
                            lock (results) results.Skipped.Add(testSkipped.FullyQualifiedName);
                            display.TestSkipped(testSkipped.DisplayName);
                            _resultCallback?.Invoke(new TestResultDetail(
                                testSkipped.FullyQualifiedName,
                                testSkipped.DisplayName,
                                "skipped",
                                0,
                                SkipReason: testSkipped.Reason
                            ));
                            break;

                        case RunCompletedEvent:
                            // Mark tests that started but didn't complete as crashed
                            foreach (var fqn in running.ToList())
                            {
                                running.Remove(fqn);
                                queue.TestCompleted(workerIndex, fqn);
                                lock (results) results.Crashed.Add(fqn);
                                var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                                display.TestCrashed(dn);
                                ReportCrashedOrHanging(fqn, "crashed", testOutput, "Test did not report completion");
                            }
                            // Tests that never started - move to suspicious for retry
                            var remainingAfterRun = queue.GetAssigned(workerIndex);
                            if (remainingAfterRun.Count > 0)
                            {
                                Log(workerIndex, $"RunCompleted but {remainingAfterRun.Count} tests never started, moving to suspicious");
                                queue.MarkSuspicious(workerIndex, remainingAfterRun);
                            }
                            break;

                        case ErrorEvent:
                            break;
                    }
                }

                // Stream ended - check for any remaining assigned tests
                var remainingAssigned = queue.GetAssigned(workerIndex);
                if (remainingAssigned.Count > 0)
                {
                    Log(workerIndex, $"Stream ended with {remainingAssigned.Count} assigned tests, moving to suspicious");
                    // Mark running tests as crashed (they started but didn't complete)
                    foreach (var fqn in running.ToList())
                    {
                        if (remainingAssigned.Contains(fqn))
                        {
                            running.Remove(fqn);
                            queue.TestCompleted(workerIndex, fqn);
                            lock (results) results.Crashed.Add(fqn);
                            var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                            display.TestCrashed(dn);
                            ReportCrashedOrHanging(fqn, "crashed", testOutput, "Stream ended while test running");
                        }
                    }
                    // Move remaining (never started) to suspicious for retry
                    var neverStarted = queue.GetAssigned(workerIndex);
                    if (neverStarted.Count > 0)
                    {
                        queue.MarkSuspicious(workerIndex, neverStarted);
                    }
                }
            }
            catch (TimeoutException)
            {
                // Get ALL assigned tests (not just running) - some may not have started yet
                var allAssigned = queue.GetAssigned(workerIndex);
                Log(workerIndex, $"TIMEOUT: running={running.Count}, assigned={allAssigned.Count}");
                // Move all assigned tests to suspicious for isolated retry
                Log(workerIndex, $"Moving {allAssigned.Count} to suspicious");
                queue.MarkSuspicious(workerIndex, allAssigned);
                foreach (var fqn in running)
                {
                    var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                    display.TestRemoved(dn);
                }
                worker.Kill();
                display.WorkerRestarting(workerIndex);
            }
            catch (WorkerCrashedException ex)
            {
                // Get ALL assigned tests (not just running) - some may not have started yet
                var allAssigned = queue.GetAssigned(workerIndex);
                Log(workerIndex, $"CRASHED: running={running.Count}, assigned={allAssigned.Count}, exit={ex.ExitCode}");
                // Move all assigned tests to suspicious for isolated retry
                Log(workerIndex, $"Moving {allAssigned.Count} to suspicious (crash)");
                queue.MarkSuspicious(workerIndex, allAssigned);
                foreach (var fqn in running)
                {
                    var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                    display.TestRemoved(dn);
                }
                display.WorkerRestarting(workerIndex);
            }
            catch (Exception ex)
            {
                Log(workerIndex, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                // Reclaim all assigned tests back to pending queue
                var reclaimed = queue.WorkerCrashed(workerIndex);
                Log(workerIndex, $"Reclaimed {reclaimed.Count} tests");
                foreach (var fqn in running)
                {
                    var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                    display.TestRemoved(dn);
                }
                worker.Kill();
                display.WorkerRestarting(workerIndex);
            }

            running.Clear();
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
                var hasNext = await enumerator.MoveNextAsync().AsTask().WaitAsync(timeout, cts.Token);

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
