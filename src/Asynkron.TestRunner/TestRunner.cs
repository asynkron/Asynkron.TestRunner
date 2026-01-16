using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Asynkron.Profiler;
using Asynkron.TestRunner.Models;
using Asynkron.TestRunner.Protocol;
using Asynkron.TestRunner.Profiling;
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
    private readonly bool _streamingConsole;
    private readonly bool _treeView;
    private readonly TreeViewSettings? _treeViewSettings;
    private readonly int _workerCount;
    private readonly bool _verbose;
    private readonly string? _logFile;
    private readonly string? _resumeFilePath;
    private readonly WorkerProfilingSettings? _profilingSettings;
    private readonly bool _ghBugReport;
    private readonly ConcurrentBag<string> _traceFiles = new();
    private readonly object _logLock = new();
    private readonly Action<TestResultDetail>? _resultCallback;
    private GitHubIssueReporter? _ghReporter;

    public TestRunner(ResultStore store, int? timeoutSeconds = null, int? hangTimeoutSeconds = null, string? filter = null, bool quiet = false, bool streamingConsole = false, bool treeView = false, TreeViewSettings? treeViewSettings = null, int workerCount = 1, bool verbose = false, string? logFile = null, string? resumeFilePath = null, WorkerProfilingSettings? profilingSettings = null, Action<TestResultDetail>? resultCallback = null, bool ghBugReport = false)
    {
        _store = store;
        _testTimeoutSeconds = timeoutSeconds ?? 30;
        _hangTimeoutSeconds = hangTimeoutSeconds ?? _testTimeoutSeconds; // Default to same as test timeout
        _filter = filter;
        _quiet = quiet;
        _streamingConsole = streamingConsole;
        _treeView = treeView;
        _treeViewSettings = treeViewSettings;
        _workerCount = Math.Max(1, workerCount);
        _verbose = verbose;
        _logFile = logFile;
        _resumeFilePath = resumeFilePath;
        _profilingSettings = profilingSettings;
        _resultCallback = resultCallback;
        _ghBugReport = ghBugReport;
    }

    private void Log(int workerIndex, string message)
    {
        if (!_verbose && _logFile == null)
        {
            return;
        }

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var line = $"[{timestamp}] Worker {workerIndex}: {message}";

        lock (_logLock)
        {
            if (_logFile != null)
            {
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }

            if (_verbose)
            {
                Console.Error.WriteLine(line);
            }
        }
    }

    private WorkerProfilingOptions? CreateWorkerProfilingOptions(string assemblyPath, string labelSuffix)
    {
        if (_profilingSettings?.Enabled != true)
        {
            return null;
        }

        var outputDirectory = Path.Combine(_store.StoreFolder, "profiles");
        var assemblyLabel = Path.GetFileNameWithoutExtension(assemblyPath);
        var label = string.IsNullOrWhiteSpace(assemblyLabel)
            ? labelSuffix
            : $"{assemblyLabel}-{labelSuffix}";
        return _profilingSettings.CreateOptions(outputDirectory, label);
    }

    private void RecordTraceFile(WorkerProcess worker)
    {
        if (string.IsNullOrWhiteSpace(worker.TraceFile))
        {
            return;
        }

        if (!File.Exists(worker.TraceFile))
        {
            return;
        }

        _traceFiles.Add(worker.TraceFile);
    }

    private void RenderProfilingResults()
    {
        if (_profilingSettings?.Enabled != true)
        {
            return;
        }

        var traceFiles = _traceFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (traceFiles.Count == 0)
        {
            return;
        }

        var analyzer = new WorkerProfileAnalyzer(_store);
        var renderer = new ProfilerConsoleRenderer();

        Console.WriteLine();
        AnsiConsole.MarkupLine("[bold]Worker Profiling Results[/]");

        foreach (var traceFile in traceFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var label = Path.GetFileNameWithoutExtension(traceFile);
            if (string.IsNullOrWhiteSpace(label))
            {
                label = traceFile;
            }

            var rootFilter = _profilingSettings.NormalizedRootFilter;

            if (_profilingSettings.Cpu)
            {
                try
                {
                    var cpuResults = analyzer.AnalyzeCpuTrace(traceFile);
                    renderer.PrintCpuResults(
                        cpuResults,
                        label,
                        description: null,
                        rootFilter: rootFilter,
                        functionFilter: null,
                        includeRuntime: false,
                        callTreeDepth: 30,
                        callTreeWidth: 4,
                        callTreeRootMode: "hottest",
                        showSelfTimeTree: false,
                        callTreeSiblingCutoffPercent: 5,
                        hotThreshold: 0.4,
                        showTimeline: false,
                        timelineWidth: 40);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]CPU profile failed for {EscapeMarkup(label)}:[/] {EscapeMarkup(ex.Message)}");
                }
            }

            if (_profilingSettings.Memory)
            {
                try
                {
                    var allocation = analyzer.AnalyzeAllocationTrace(traceFile);
                    var memoryResults = BuildMemoryProfileResult(allocation);
                    renderer.PrintMemoryResults(
                        memoryResults,
                        label,
                        description: null,
                        callTreeRoot: rootFilter,
                        includeRuntime: false,
                        callTreeDepth: 30,
                        callTreeWidth: 4,
                        callTreeSiblingCutoffPercent: 5);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Memory profile failed for {EscapeMarkup(label)}:[/] {EscapeMarkup(ex.Message)}");
                }
            }

            if (_profilingSettings.Exception)
            {
                try
                {
                    var exceptionResults = analyzer.AnalyzeExceptionTrace(traceFile);
                    renderer.PrintExceptionResults(
                        exceptionResults,
                        label,
                        description: null,
                        rootFilter: rootFilter,
                        exceptionTypeFilter: null,
                        functionFilter: null,
                        includeRuntime: false,
                        callTreeDepth: 30,
                        callTreeWidth: 4,
                        callTreeRootMode: "hottest",
                        callTreeSiblingCutoffPercent: 5);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Exception profile failed for {EscapeMarkup(label)}:[/] {EscapeMarkup(ex.Message)}");
                }
            }

            if (_profilingSettings.Latency)
            {
                try
                {
                    var contentionResults = analyzer.AnalyzeContentionTrace(traceFile);
                    renderer.PrintContentionResults(
                        contentionResults,
                        label,
                        description: null,
                        rootFilter: rootFilter,
                        functionFilter: null,
                        includeRuntime: false,
                        callTreeDepth: 30,
                        callTreeWidth: 4,
                        callTreeRootMode: "hottest",
                        callTreeSiblingCutoffPercent: 5);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Contention profile failed for {EscapeMarkup(label)}:[/] {EscapeMarkup(ex.Message)}");
                }
            }
        }
    }

    private static MemoryProfileResult BuildMemoryProfileResult(AllocationCallTreeResult allocation)
    {
        var allocationEntries = allocation.TypeRoots
            .OrderByDescending(root => root.TotalBytes)
            .Take(50)
            .Select(root => new AllocationEntry(root.Name, root.Count, FormatBytes(root.TotalBytes)))
            .ToList();

        var totalAllocated = FormatBytes(allocation.TotalBytes);

        return new MemoryProfileResult(
            Iterations: null,
            TotalTime: null,
            PerIterationTime: null,
            TotalAllocated: null,
            PerIterationAllocated: null,
            Gen0Collections: null,
            Gen1Collections: null,
            Gen2Collections: null,
            ParseAllocated: null,
            EvaluateAllocated: null,
            HeapBefore: null,
            HeapAfter: null,
            AllocationTotal: totalAllocated,
            AllocationEntries: allocationEntries,
            AllocationCallTree: allocation,
            AllocationByTypeRaw: null,
            RawOutput: null);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024d).ToString("F2", CultureInfo.InvariantCulture) + " KB";
        }

        if (bytes < 1024L * 1024L * 1024L)
        {
            return (bytes / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) + " MB";
        }

        return (bytes / (1024d * 1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture) + " GB";
    }

    private static void SeedResultsFromResume(ResumeTracker resumeTracker, TestResults results, LiveDisplay? display)
    {
        foreach (var entry in resumeTracker.CompletedEntries)
        {
            var displayName = entry.DisplayName ?? entry.Test;
            switch (entry.Status)
            {
                case "passed":
                    results.AddPassed(entry.Test);
                    display?.TestPassed(displayName);
                    break;
                case "failed":
                    results.AddFailed(entry.Test);
                    display?.TestFailed(displayName);
                    break;
                case "skipped":
                    results.AddSkipped(entry.Test);
                    display?.TestSkipped(displayName);
                    break;
                case "hanging":
                    results.AddHanging(entry.Test);
                    display?.TestHanging(displayName);
                    break;
                case "crashed":
                    results.AddCrashed(entry.Test);
                    display?.TestCrashed(displayName);
                    break;
            }
        }
    }

    private static void MarkResume(ResumeTracker? resumeTracker, string status, string testName, string? displayName)
    {
        resumeTracker?.MarkCompleted(testName, status, displayName);
    }

    private string? GetResumeFilePath()
    {
        if (_resumeFilePath == null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(_resumeFilePath)
            ? Path.Combine(_store.BaseFolder, "resume.jsonl")
            : _resumeFilePath;
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
                var discovered = await discoveryWorker.DiscoverAsync(assemblyPath, ct);
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
                {
                    AnsiConsole.MarkupLine($"[yellow]No tests match filter:[/] {_filter}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]No tests found[/]");
                }

                continue;
            }

            AnsiConsole.MarkupLine($"[dim]Found {allTests.Count} tests[/]");

            // Initialize GitHub reporter if enabled
            if (_ghBugReport)
            {
                _ghReporter = new GitHubIssueReporter(assemblyPath, _verbose);
            }

            // Run with resilient recovery
            if (_streamingConsole)
            {
                await RunWithRecoveryStreamingAsync(assemblyPath, allTests, results, ct);
            }
            else if (_quiet)
            {
                await RunWithRecoveryQuietAsync(assemblyPath, allTests, results, ct);
            }
            else if (_treeView)
            {
                await RunWithRecoveryTreeViewAsync(assemblyPath, allTests, results, ct);
            }
            else
            {
                await RunWithRecoveryLiveAsync(assemblyPath, allTests, results, ct);
            }
        }

        stopwatch.Stop();
        PrintSummary(results, stopwatch.Elapsed);
        SaveResults(results, stopwatch.Elapsed);
        RenderProfilingResults();

        // Report failures to GitHub if enabled
        if (_ghBugReport && _ghReporter != null)
        {
            try
            {
                var ghResult = await _ghReporter.ReportFailuresAsync(ct);
                if (ghResult.Created > 0 || ghResult.Matched > 0)
                {
                    AnsiConsole.MarkupLine($"[dim]GitHub Issues: {ghResult.Created} created, {ghResult.Matched} already exist, {ghResult.Skipped} skipped[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Failed to report to GitHub: {ex.Message}");
            }
        }

        return results.Failed.Count > 0 || results.Crashed.Count > 0 || results.Hanging.Count > 0 ? 1 : 0;
    }

    private async Task RunWithRecoveryLiveAsync(string assemblyPath, List<string> allTests, TestResults results, CancellationToken ct)
    {
        var display = new LiveDisplay();
        display.SetFilter(_filter);
        display.SetAssembly(assemblyPath);
        display.SetWorkerCount(_workerCount);
        display.SetTimeout(_testTimeoutSeconds);

        var resumeTracker = ResumeTracker.TryLoad(GetResumeFilePath(), assemblyPath, allTests);
        var totalTests = resumeTracker?.AllTests.Count ?? allTests.Count;
        display.SetTotal(totalTests);

        if (resumeTracker != null)
        {
            SeedResultsFromResume(resumeTracker, results, display);
        }

        var pendingTests = resumeTracker?.FilterPending() ?? allTests;

        var failureDetails = new List<(string Name, string Message, string? Stack)>();
        var failureLock = new object();

        // Shared work queue - workers pull from this
        var queue = new WorkQueue(pendingTests);
        var initialBatchSize = Math.Max(50, pendingTests.Count / _workerCount / 4);
        var batchSizeHolder = new BatchSizeHolder(initialBatchSize);
        var tier = 1;

        await AnsiConsole.Live(display.Render())
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                void UpdateDisplay() => ctx.UpdateTarget(display.Render());

                // Run all workers in parallel - they pull batches from shared queue
                var workerTasks = Enumerable.Range(0, _workerCount)
                    .Select(index => RunWorkerAsync(assemblyPath, index, queue, batchSizeHolder, results, display, resumeTracker, failureDetails, failureLock, UpdateDisplay, ct))
                    .ToList();

                // Monitor loop: refresh display and handle tier promotion
                while (true)
                {
                    display.SetQueueStats(queue.PendingCount, queue.SuspiciousCount, queue.ConfirmedCount, batchSizeHolder.Size);
                    UpdateDisplay();

                    // Check if we need to promote suspicious tests (tier escalation)
                    if (!queue.HasPendingWork && queue.SuspiciousCount > 0 && queue.AllWorkersIdle)
                    {
                        var promoted = queue.PromoteSuspicious();
                        // Large batches: halve to clear good tests quickly
                        // Once under 100: go straight to isolation
                        var current = batchSizeHolder.Size;
                        var newBatchSize = current > 100 ? current / 2 : 1;
                        batchSizeHolder.Size = newBatchSize;
                        tier++;
                        Log(-1, $"TIER {tier}: Promoted {promoted} suspicious tests, batch size {current}→{newBatchSize}");
                    }

                    // Check if all work is done
                    if (queue.IsComplete)
                    {
                        break;
                    }

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
                    {
                        AnsiConsole.MarkupLine($"[dim]    {EscapeMarkup(firstLine)}[/]");
                    }
                }
            }
            if (failureDetails.Count > 10)
            {
                AnsiConsole.MarkupLine($"[dim]  ...and {failureDetails.Count - 10} more[/]");
            }
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

    private async Task RunWithRecoveryTreeViewAsync(string assemblyPath, List<string> allTests, TestResults results, CancellationToken ct)
    {
        var display = new TreeViewDisplay(_treeViewSettings);
        display.SetFilter(_filter);
        display.SetAssembly(assemblyPath);
        display.SetWorkerCount(_workerCount);
        display.Initialize(allTests);

        var resumeTracker = ResumeTracker.TryLoad(GetResumeFilePath(), assemblyPath, allTests);

        if (resumeTracker != null)
        {
            SeedResultsFromResumeTreeView(resumeTracker, results, display);
        }

        var pendingTests = resumeTracker?.FilterPending() ?? allTests;

        var failureDetails = new List<(string Name, string Message, string? Stack)>();
        var failureLock = new object();

        // Shared work queue - workers pull from this
        var queue = new WorkQueue(pendingTests);
        var initialBatchSize = Math.Max(50, pendingTests.Count / _workerCount / 4);
        var batchSizeHolder = new BatchSizeHolder(initialBatchSize);
        var tier = 1;

        // Set up keyboard handling for scrolling
        var scrollCts = new CancellationTokenSource();
        var scrollTask = Task.Run(async () =>
        {
            while (!scrollCts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow:
                            display.ScrollUp();
                            break;
                        case ConsoleKey.DownArrow:
                            display.ScrollDown();
                            break;
                        case ConsoleKey.PageUp:
                            display.PageUp();
                            break;
                        case ConsoleKey.PageDown:
                            display.PageDown();
                            break;
                    }
                }
                await Task.Delay(50, scrollCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }, scrollCts.Token);

        try
        {
            await AnsiConsole.Live(display.Render())
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    void UpdateDisplay() => ctx.UpdateTarget(display.Render());

                    // Run all workers in parallel - they pull batches from shared queue
                    var workerTasks = Enumerable.Range(0, _workerCount)
                        .Select(index => RunWorkerTreeViewAsync(assemblyPath, index, queue, batchSizeHolder, results, display, resumeTracker, failureDetails, failureLock, UpdateDisplay, ct))
                        .ToList();

                    // Monitor loop: refresh display and handle tier promotion
                    while (true)
                    {
                        display.SetQueueStats(queue.PendingCount, queue.SuspiciousCount, queue.ConfirmedCount, batchSizeHolder.Size);
                        UpdateDisplay();

                        // Check if we need to promote suspicious tests (tier escalation)
                        if (!queue.HasPendingWork && queue.SuspiciousCount > 0 && queue.AllWorkersIdle)
                        {
                            var promoted = queue.PromoteSuspicious();
                            var current = batchSizeHolder.Size;
                            var newBatchSize = current > 100 ? current / 2 : 1;
                            batchSizeHolder.Size = newBatchSize;
                            tier++;
                            Log(-1, $"TIER {tier}: Promoted {promoted} suspicious tests, batch size {current}→{newBatchSize}");
                        }

                        // Check if all work is done
                        if (queue.IsComplete)
                        {
                            break;
                        }

                        await Task.Delay(100, ct);
                    }

                    // Wait for workers to finish
                    await Task.WhenAll(workerTasks);
                    UpdateDisplay();
                });
        }
        finally
        {
            scrollCts.Cancel();
            try { await scrollTask; } catch { }
        }

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
                    {
                        AnsiConsole.MarkupLine($"[dim]    {EscapeMarkup(firstLine)}[/]");
                    }
                }
            }
            if (failureDetails.Count > 10)
            {
                AnsiConsole.MarkupLine($"[dim]  ...and {failureDetails.Count - 10} more[/]");
            }
        }
    }

    private static void SeedResultsFromResumeTreeView(ResumeTracker resumeTracker, TestResults results, TreeViewDisplay display)
    {
        foreach (var entry in resumeTracker.CompletedEntries)
        {
            switch (entry.Status)
            {
                case "passed":
                    results.AddPassed(entry.Test);
                    display.TestPassed(entry.Test);
                    break;
                case "failed":
                    results.AddFailed(entry.Test);
                    display.TestFailed(entry.Test);
                    break;
                case "skipped":
                    results.AddSkipped(entry.Test);
                    display.TestSkipped(entry.Test);
                    break;
                case "hanging":
                    results.AddHanging(entry.Test);
                    display.TestHanging(entry.Test);
                    break;
                case "crashed":
                    results.AddCrashed(entry.Test);
                    display.TestCrashed(entry.Test);
                    break;
            }
        }
    }

    /// <summary>
    /// Worker loop for tree view: pulls batches from shared queue until no work remains
    /// </summary>
    private async Task RunWorkerTreeViewAsync(
        string assemblyPath,
        int workerIndex,
        WorkQueue queue,
        BatchSizeHolder batchSizeHolder,
        TestResults results,
        TreeViewDisplay display,
        ResumeTracker? resumeTracker,
        List<(string Name, string Message, string? Stack)> failureDetails,
        object failureLock,
        Action updateDisplay,
        CancellationToken ct)
    {
        var running = new HashSet<string>();
        var testOutput = new Dictionary<string, StringBuilder>();
        var testStartTimes = new Dictionary<string, DateTime>();
        var fqnToDisplayName = new Dictionary<string, string>();

        Log(workerIndex, "Worker started (tree view)");

        while (!ct.IsCancellationRequested)
        {
            // Pull next batch from queue with current batch size
            var batch = queue.TakeBatch(workerIndex, batchSizeHolder.Size);
            if (batch.Count == 0)
            {
                // No pending work - check if there's suspicious/confirmed work pending tier promotion
                if (queue.SuspiciousCount > 0 || queue.ConfirmedCount > 0)
                {
                    // Wait for monitor to promote tests
                    await Task.Delay(100, ct);
                    continue;
                }

                // All work done
                break;
            }

            Log(workerIndex, $"Running batch of {batch.Count} tests");
            display.WorkerActivity(workerIndex);
            display.SetWorkerBatch(workerIndex, 0, batch.Count);

            var profiling = CreateWorkerProfilingOptions(assemblyPath, $"worker{workerIndex}");
            await using var worker = WorkerProcess.Spawn(profiling: profiling);

            try
            {
                using var cts = new CancellationTokenSource();

                await foreach (var msg in worker.RunAsync(assemblyPath, batch, _testTimeoutSeconds, cts.Token)
                    .WithTimeout(TimeSpan.FromSeconds(_hangTimeoutSeconds), cts))
                {
                    display.WorkerActivity(workerIndex);

                    switch (msg)
                    {
                        case TestStartedEvent started:
                            running.Add(started.FullyQualifiedName);
                            testStartTimes[started.FullyQualifiedName] = DateTime.UtcNow;
                            fqnToDisplayName[started.FullyQualifiedName] = started.DisplayName;
                            display.TestStarted(started.DisplayName);
                            break;

                        case TestPassedEvent testPassed:
                            running.Remove(testPassed.FullyQualifiedName);
                            queue.TestCompleted(workerIndex, testPassed.FullyQualifiedName);
                            results.AddPassed(testPassed.FullyQualifiedName);
                            display.TestPassed(testPassed.FullyQualifiedName);
                            MarkResume(resumeTracker, "passed", testPassed.FullyQualifiedName, testPassed.DisplayName);
                            _resultCallback?.Invoke(new TestResultDetail(
                                testPassed.FullyQualifiedName,
                                testPassed.DisplayName,
                                "passed",
                                testPassed.DurationMs
                            ));
                            display.WorkerTestPassed(workerIndex);
                            break;

                        case TestFailedEvent testFailed:
                            running.Remove(testFailed.FullyQualifiedName);
                            queue.TestCompleted(workerIndex, testFailed.FullyQualifiedName);
                            results.AddFailed(testFailed.FullyQualifiedName);
                            display.TestFailed(testFailed.FullyQualifiedName);
                            MarkResume(resumeTracker, "failed", testFailed.FullyQualifiedName, testFailed.DisplayName);
                            lock (failureLock)
                            {
                                failureDetails.Add((testFailed.DisplayName, testFailed.ErrorMessage, testFailed.StackTrace));
                            }
                            var failedOutput = testOutput.TryGetValue(testFailed.FullyQualifiedName, out var output) ? output.ToString() : null;
                            _resultCallback?.Invoke(new TestResultDetail(
                                testFailed.FullyQualifiedName,
                                testFailed.DisplayName,
                                "failed",
                                testFailed.DurationMs,
                                testFailed.ErrorMessage,
                                testFailed.StackTrace,
                                Output: failedOutput
                            ));
                            _ghReporter?.AddFailedTest(testFailed.FullyQualifiedName, testFailed.DisplayName, testFailed.ErrorMessage, testFailed.StackTrace, failedOutput);
                            testOutput.Remove(testFailed.FullyQualifiedName);
                            display.WorkerTestFailed(workerIndex);
                            break;

                        case TestSkippedEvent testSkipped:
                            running.Remove(testSkipped.FullyQualifiedName);
                            queue.TestCompleted(workerIndex, testSkipped.FullyQualifiedName);
                            results.AddSkipped(testSkipped.FullyQualifiedName);
                            display.TestSkipped(testSkipped.FullyQualifiedName);
                            MarkResume(resumeTracker, "skipped", testSkipped.FullyQualifiedName, testSkipped.DisplayName);
                            _resultCallback?.Invoke(new TestResultDetail(
                                testSkipped.FullyQualifiedName,
                                testSkipped.DisplayName,
                                "skipped",
                                0,
                                SkipReason: testSkipped.Reason
                            ));
                            break;

                        case TestOutputEvent testOutput2:
                            if (!testOutput.TryGetValue(testOutput2.FullyQualifiedName, out var sb))
                            {
                                sb = new StringBuilder();
                                testOutput[testOutput2.FullyQualifiedName] = sb;
                            }
                            sb.AppendLine(testOutput2.Text);
                            break;

                        case RunCompletedEvent:
                            // Mark any still-running tests as crashed
                            foreach (var fqn in running.ToList())
                            {
                                running.Remove(fqn);
                                queue.TestCompleted(workerIndex, fqn);
                                results.AddCrashed(fqn);
                                display.TestCrashed(fqn);
                                var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                                MarkResume(resumeTracker, "crashed", fqn, dn);
                                ReportCrashedOrHanging(fqn, "crashed", testOutput);
                                display.WorkerTestCrashed(workerIndex);
                            }
                            break;

                        case ErrorEvent:
                            break;
                    }

                    updateDisplay();
                }
            }
            catch (TimeoutException)
            {
                // Running tests caused the timeout - fast track to isolation
                Log(workerIndex, $"TIMEOUT: {running.Count} running → confirmed, rest → suspicious");
                queue.MarkConfirmed(workerIndex, running);
                foreach (var fqn in running)
                {
                    var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                    display.TestRemoved(dn);
                }
                var neverStarted = queue.GetAssigned(workerIndex);
                if (neverStarted.Count > 0)
                {
                    Log(workerIndex, $"{neverStarted.Count} never started → suspicious");
                    queue.MarkSuspicious(workerIndex, neverStarted);
                }
                worker.Kill();
                display.WorkerRestarting(workerIndex);
            }
            catch (WorkerCrashedException ex)
            {
                Log(workerIndex, $"CRASHED: {running.Count} running → confirmed, rest → suspicious, exit={ex.ExitCode}");
                queue.MarkConfirmed(workerIndex, running);
                foreach (var fqn in running)
                {
                    var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                    display.TestRemoved(dn);
                }
                var neverStarted = queue.GetAssigned(workerIndex);
                if (neverStarted.Count > 0)
                {
                    Log(workerIndex, $"{neverStarted.Count} never started → suspicious");
                    queue.MarkSuspicious(workerIndex, neverStarted);
                }
                display.WorkerRestarting(workerIndex);
            }
            catch (Exception ex)
            {
                Log(workerIndex, $"EXCEPTION: {ex.GetType().Name}: {ex.Message}");
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
            finally
            {
                RecordTraceFile(worker);
            }

            running.Clear();
        }

        Log(workerIndex, "COMPLETE (tree view)");
        display.WorkerComplete(workerIndex);
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
        ResumeTracker? resumeTracker,
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

            var profiling = CreateWorkerProfilingOptions(assemblyPath, $"worker-{workerIndex}");
            await using var worker = WorkerProcess.Spawn(profiling: profiling);

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
                            lock (results)
                            {
                                results.AddHanging(fqn);
                            }

                            var displayName = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                            display.TestHanging(displayName);
                            MarkResume(resumeTracker, "hanging", fqn, displayName);
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
                            lock (results)
                            {
                                results.AddPassed(testPassed.FullyQualifiedName);
                            }

                            display.TestPassed(testPassed.DisplayName);
                            MarkResume(resumeTracker, "passed", testPassed.FullyQualifiedName, testPassed.DisplayName);
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
                            lock (results)
                            {
                                results.AddFailed(testFailed.FullyQualifiedName);
                            }

                            display.TestFailed(testFailed.DisplayName);
                            MarkResume(resumeTracker, "failed", testFailed.FullyQualifiedName, testFailed.DisplayName);
                            lock (failureLock)
                            {
                                failureDetails.Add((testFailed.DisplayName, testFailed.ErrorMessage, testFailed.StackTrace));
                            }

                            var failedOutput2 = testOutput.GetValueOrDefault(testFailed.FullyQualifiedName)?.ToString();
                            _resultCallback?.Invoke(new TestResultDetail(
                                testFailed.FullyQualifiedName,
                                testFailed.DisplayName,
                                "failed",
                                testFailed.DurationMs,
                                testFailed.ErrorMessage,
                                testFailed.StackTrace,
                                failedOutput2
                            ));
                            _ghReporter?.AddFailedTest(testFailed.FullyQualifiedName, testFailed.DisplayName, testFailed.ErrorMessage, testFailed.StackTrace, failedOutput2);
                            testOutput.Remove(testFailed.FullyQualifiedName);
                            break;

                        case TestSkippedEvent testSkipped:
                            running.Remove(testSkipped.FullyQualifiedName);
                            queue.TestCompleted(workerIndex, testSkipped.FullyQualifiedName);
                            completedInBatch++;
                            lock (results)
                            {
                                results.AddSkipped(testSkipped.FullyQualifiedName);
                            }

                            display.TestSkipped(testSkipped.DisplayName);
                            MarkResume(resumeTracker, "skipped", testSkipped.FullyQualifiedName, testSkipped.DisplayName);
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
                                lock (results)
                                {
                                    results.AddCrashed(fqn);
                                }

                                var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                                display.TestCrashed(dn);
                                MarkResume(resumeTracker, "crashed", fqn, dn);
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
                            lock (results)
                            {
                                results.AddCrashed(fqn);
                            }

                            var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                            display.TestCrashed(dn);
                            MarkResume(resumeTracker, "crashed", fqn, dn);
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
                // Running tests caused the timeout - fast track to isolation
                Log(workerIndex, $"TIMEOUT: {running.Count} running → confirmed, rest → suspicious");
                queue.MarkConfirmed(workerIndex, running);
                foreach (var fqn in running)
                {
                    var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                    display.TestRemoved(dn);
                }
                // Never-started tests go to suspicious (they might be fine)
                var neverStarted = queue.GetAssigned(workerIndex);
                if (neverStarted.Count > 0)
                {
                    Log(workerIndex, $"{neverStarted.Count} never started → suspicious");
                    queue.MarkSuspicious(workerIndex, neverStarted);
                }
                worker.Kill();
                display.WorkerRestarting(workerIndex);
            }
            catch (WorkerCrashedException ex)
            {
                // Running tests caused the crash - fast track to isolation
                Log(workerIndex, $"CRASHED: {running.Count} running → confirmed, rest → suspicious, exit={ex.ExitCode}");
                queue.MarkConfirmed(workerIndex, running);
                foreach (var fqn in running)
                {
                    var dn = fqnToDisplayName.GetValueOrDefault(fqn, fqn);
                    display.TestRemoved(dn);
                }
                // Never-started tests go to suspicious
                var neverStarted = queue.GetAssigned(workerIndex);
                if (neverStarted.Count > 0)
                {
                    Log(workerIndex, $"{neverStarted.Count} never started → suspicious");
                    queue.MarkSuspicious(workerIndex, neverStarted);
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
            finally
            {
                RecordTraceFile(worker);
            }

            running.Clear();
        }

        Log(workerIndex, "COMPLETE");
        display.WorkerComplete(workerIndex);
    }

    private async Task RunWithRecoveryQuietAsync(string assemblyPath, List<string> allTests, TestResults results, CancellationToken ct)
    {
        var resumeTracker = ResumeTracker.TryLoad(GetResumeFilePath(), assemblyPath, allTests);
        if (resumeTracker != null)
        {
            SeedResultsFromResume(resumeTracker, results, null);
        }

        var pendingTests = resumeTracker?.FilterPending() ?? allTests;
        var pending = new HashSet<string>(pendingTests);
        var running = new HashSet<string>();
        var attempts = 0;
        const int maxAttempts = 100;

        while (pending.Count > 0 && attempts < maxAttempts)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            attempts++;
            running.Clear();

            var profiling = CreateWorkerProfilingOptions(assemblyPath, $"recovery-{attempts}");
            await using var worker = WorkerProcess.Spawn(profiling: profiling);

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
                            results.AddPassed(testPassed.FullyQualifiedName);
                            MarkResume(resumeTracker, "passed", testPassed.FullyQualifiedName, testPassed.DisplayName);
                            break;

                        case TestFailedEvent testFailed:
                            running.Remove(testFailed.FullyQualifiedName);
                            pending.Remove(testFailed.FullyQualifiedName);
                            results.AddFailed(testFailed.FullyQualifiedName);
                            MarkResume(resumeTracker, "failed", testFailed.FullyQualifiedName, testFailed.DisplayName);
                            _ghReporter?.AddFailedTest(testFailed.FullyQualifiedName, testFailed.DisplayName, testFailed.ErrorMessage, testFailed.StackTrace, null);
                            AnsiConsole.MarkupLine($"[red]  ✗[/] {EscapeMarkup(testFailed.DisplayName)}");
                            break;

                        case TestSkippedEvent testSkipped:
                            running.Remove(testSkipped.FullyQualifiedName);
                            pending.Remove(testSkipped.FullyQualifiedName);
                            results.AddSkipped(testSkipped.FullyQualifiedName);
                            MarkResume(resumeTracker, "skipped", testSkipped.FullyQualifiedName, testSkipped.DisplayName);
                            break;

                        case RunCompletedEvent:
                            // Mark any still-running tests as crashed
                            foreach (var fqn in running.ToList())
                            {
                                running.Remove(fqn);
                                pending.Remove(fqn);
                                results.AddCrashed(fqn);
                                MarkResume(resumeTracker, "crashed", fqn, fqn);
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
                    results.AddHanging(fqn);
                    MarkResume(resumeTracker, "hanging", fqn, fqn);
                    AnsiConsole.MarkupLine($"[red]  ⏱[/] {fqn} [red](HANGING)[/]");
                }
                worker.Kill();
            }
            catch (WorkerCrashedException ex)
            {
                foreach (var fqn in running)
                {
                    pending.Remove(fqn);
                    results.AddCrashed(fqn);
                    MarkResume(resumeTracker, "crashed", fqn, fqn);
                    AnsiConsole.MarkupLine($"[red]  💥[/] {fqn} [red](CRASHED: {ex.ExitCode})[/]");
                }
            }
            catch (Exception)
            {
                worker.Kill();
                break;
            }
            finally
            {
                RecordTraceFile(worker);
            }
        }
    }

    private async Task RunWithRecoveryStreamingAsync(string assemblyPath, List<string> allTests, TestResults results, CancellationToken ct)
    {
        var resumeTracker = ResumeTracker.TryLoad(GetResumeFilePath(), assemblyPath, allTests);
        if (resumeTracker != null)
        {
            SeedResultsFromResume(resumeTracker, results, null);
        }

        var pendingTests = resumeTracker?.FilterPending() ?? allTests;
        var pending = new HashSet<string>(pendingTests);
        var running = new HashSet<string>();
        var attempts = 0;
        const int maxAttempts = 100;

        while (pending.Count > 0 && attempts < maxAttempts)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            attempts++;
            running.Clear();

            var profiling = CreateWorkerProfilingOptions(assemblyPath, $"recovery-{attempts}");
            await using var worker = WorkerProcess.Spawn(profiling: profiling);

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
                            results.AddPassed(testPassed.FullyQualifiedName);
                            MarkResume(resumeTracker, "passed", testPassed.FullyQualifiedName, testPassed.DisplayName);
                            Console.WriteLine($"\x1b[92m{"[PASSED]",-10}\x1b[0m {testPassed.DisplayName} ({testPassed.DurationMs:F0}ms)");
                            break;

                        case TestFailedEvent testFailed:
                            running.Remove(testFailed.FullyQualifiedName);
                            pending.Remove(testFailed.FullyQualifiedName);
                            results.AddFailed(testFailed.FullyQualifiedName);
                            MarkResume(resumeTracker, "failed", testFailed.FullyQualifiedName, testFailed.DisplayName);
                            _ghReporter?.AddFailedTest(testFailed.FullyQualifiedName, testFailed.DisplayName, testFailed.ErrorMessage, testFailed.StackTrace, null);
                            Console.WriteLine($"\x1b[91m{"[FAILED]",-10}\x1b[0m {testFailed.DisplayName}");
                            break;

                        case TestSkippedEvent testSkipped:
                            running.Remove(testSkipped.FullyQualifiedName);
                            pending.Remove(testSkipped.FullyQualifiedName);
                            results.AddSkipped(testSkipped.FullyQualifiedName);
                            MarkResume(resumeTracker, "skipped", testSkipped.FullyQualifiedName, testSkipped.DisplayName);
                            Console.WriteLine($"\x1b[93m{"[SKIPPED]",-10}\x1b[0m {testSkipped.DisplayName}");
                            break;

                        case RunCompletedEvent:
                            // Mark any still-running tests as crashed
                            foreach (var fqn in running.ToList())
                            {
                                running.Remove(fqn);
                                pending.Remove(fqn);
                                results.AddCrashed(fqn);
                                MarkResume(resumeTracker, "crashed", fqn, fqn);
                                _ghReporter?.AddFailedTest(fqn, fqn, "Test crashed (no result received)", null, null);
                                Console.WriteLine($"\x1b[91m{"[CRASHED]",-10}\x1b[0m {fqn}");
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
                    results.AddHanging(fqn);
                    MarkResume(resumeTracker, "hanging", fqn, fqn);
                    Console.WriteLine($"\x1b[93m{"[HANGING]",-10}\x1b[0m {fqn}");
                }
                worker.Kill();
            }
            catch (WorkerCrashedException)
            {
                foreach (var fqn in running)
                {
                    pending.Remove(fqn);
                    results.AddCrashed(fqn);
                    MarkResume(resumeTracker, "crashed", fqn, fqn);
                    Console.WriteLine($"\x1b[91m{"[CRASHED]",-10}\x1b[0m {fqn}");
                }
            }
            catch (Exception)
            {
                worker.Kill();
                break;
            }
            finally
            {
                RecordTraceFile(worker);
            }
        }
    }

    private static void PrintSummary(TestResults results, TimeSpan elapsed)
    {
        Console.WriteLine();

        ChartRenderer.RenderFooterSummary(
            results.Passed.Count,
            results.Failed.Count,
            results.Skipped.Count,
            results.Hanging.Count,
            results.Crashed.Count,
            elapsed);
        if (results.Hanging.Count > 0)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine($"[red]⏱ Hanging tests ({results.Hanging.Count}):[/]");
            foreach (var test in results.Hanging.Take(10))
            {
                AnsiConsole.MarkupLine($"  [red]→[/] {test}");
            }

            if (results.Hanging.Count > 10)
            {
                AnsiConsole.MarkupLine($"  [dim]...and {results.Hanging.Count - 10} more[/]");
            }
        }

        if (results.Crashed.Count > 0)
        {
            Console.WriteLine();
            AnsiConsole.MarkupLine($"[red]💥 Crashed tests ({results.Crashed.Count}):[/]");
            foreach (var test in results.Crashed.Take(10))
            {
                AnsiConsole.MarkupLine($"  [red]→[/] {test}");
            }

            if (results.Crashed.Count > 10)
            {
                AnsiConsole.MarkupLine($"  [dim]...and {results.Crashed.Count - 10} more[/]");
            }
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
            TimedOutTests = results.Hanging.Concat(results.Crashed).ToList(),
            CompletionOrder = results.CompletionOrder.ToList()
        };

        _store.SaveResult(result);

        var regressions = new List<string>();
        var fixes = new List<string>();
        var flakyTests = new List<string>();
        const int flakyHistoryCount = 5;
        const int historyChartCount = 10;
        var recentRuns = _store.GetRecentRuns(historyChartCount);
        var flakyRuns = recentRuns.Take(flakyHistoryCount).ToList();
        if (recentRuns.Count >= 2)
        {
            var previous = recentRuns[1];
            regressions = result.GetRegressions(previous);
            fixes = result.GetFixes(previous);
        }

        flakyTests = TestRunResult.GetFlakyTests(flakyRuns);

        var reportPath = WriteMarkdownReport(results, elapsed, regressions, fixes, flakyTests, flakyRuns.Count);
        if (!string.IsNullOrEmpty(reportPath))
        {
            Console.WriteLine();
            Console.WriteLine($"Full summary {reportPath}");
        }

        ChartRenderer.RenderHistory(recentRuns);
        if (recentRuns.Count >= 2)
        {
            ChartRenderer.RenderRegressions(recentRuns[0], recentRuns[1]);
        }

        ChartRenderer.RenderFlakyTests(flakyTests, flakyRuns.Count);
    }

    private string? WriteMarkdownReport(
        TestResults results,
        TimeSpan elapsed,
        IReadOnlyList<string> regressions,
        IReadOnlyList<string> fixes,
        IReadOnlyList<string> flakyTests,
        int flakyRunCount)
    {
        try
        {
            var reportPath = Path.Combine(_store.BaseFolder, "summary.md");
            Directory.CreateDirectory(_store.BaseFolder);

            var builder = new StringBuilder();
            builder.AppendLine("# Test Runner Summary");
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Duration: {elapsed.TotalSeconds:F1}s");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Passed: {results.Passed.Count}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Failed: {results.Failed.Count}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Skipped: {results.Skipped.Count}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Hanging: {results.Hanging.Count}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Crashed: {results.Crashed.Count}");

            AppendSection(builder, "Failed", results.Failed);
            AppendSection(builder, "Skipped", results.Skipped);
            AppendSection(builder, "Hanging", results.Hanging);
            AppendSection(builder, "Crashed", results.Crashed);
            AppendSection(builder, "Regressions", regressions);
            AppendSection(builder, "Fixes", fixes);
            AppendSection(builder, $"Flaky (last {flakyRunCount} runs)", flakyTests);

            File.WriteAllText(reportPath, builder.ToString());
            return reportPath;
        }
        catch
        {
            return null;
        }
    }

    private static void AppendSection(StringBuilder builder, string title, IEnumerable<string> items)
    {
        var list = items.OrderBy(item => item).ToList();
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"## {title} ({list.Count})");
        if (list.Count == 0)
        {
            builder.AppendLine();
            builder.AppendLine("None");
            return;
        }

        foreach (var item in list)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {item}");
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
    public List<SlotStatus> CompletionOrder { get; } = new();

    public void AddPassed(string testName)
    {
        Passed.Add(testName);
        CompletionOrder.Add(SlotStatus.Passed);
    }

    public void AddFailed(string testName)
    {
        Failed.Add(testName);
        CompletionOrder.Add(SlotStatus.Failed);
    }

    public void AddSkipped(string testName)
    {
        Skipped.Add(testName);
        CompletionOrder.Add(SlotStatus.Passed);
    }

    public void AddHanging(string testName)
    {
        Hanging.Add(testName);
        CompletionOrder.Add(SlotStatus.Hanging);
    }

    public void AddCrashed(string testName)
    {
        Crashed.Add(testName);
        CompletionOrder.Add(SlotStatus.Crashed);
    }
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
                {
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }
}
