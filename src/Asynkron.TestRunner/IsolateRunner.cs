using System.Diagnostics;
using Spectre.Console;

namespace Asynkron.TestRunner;

/// <summary>
/// Result of an isolation run, containing isolated hanging tests and failed batches.
/// </summary>
public record IsolationResult(
    List<string> IsolatedHangingTests,
    List<string> FailedBatches,
    int TotalBatches,
    int PassedBatches,
    TimeSpan TotalDuration);

public class IsolateRunner
{
    private const int MaxTestsPerBatch = 5000;
    private const int MaxRecursionDepth = 10; // Prevent infinite recursion

    private readonly TimeoutStrategy _timeoutStrategy;
    private readonly string[] _baseTestArgs;
    private readonly string? _initialFilter;
    private TestTree? _testTree;
    private int _currentAttempt = 1; // Track attempt number for graduated timeouts
    private readonly int _maxParallelBatches;
    private readonly object _consoleLock = new();

    // Track isolated hanging tests across recursive runs
    private readonly List<string> _isolatedHangingTests = [];
    private readonly List<string> _failedBatches = [];
    private readonly HashSet<string> _completedBatches = []; // Batches we've fully processed

    /// <summary>
    /// Gets the list of isolated hanging tests after a run.
    /// </summary>
    public IReadOnlyList<string> IsolatedHangingTests => _isolatedHangingTests;

    /// <summary>
    /// Gets the list of failed (non-hanging) batches after a run.
    /// </summary>
    public IReadOnlyList<string> FailedBatches => _failedBatches;

    private record TestBatch(string Label, List<string> Tests, List<string> FilterPrefixes);

    private record BatchRunResult(
        string Label,
        int TotalTests,
        HashSet<string> Passed,
        HashSet<string> Failed,
        HashSet<string> TimedOut,
        int ExitCode,
        bool Hung,
        bool HadResults,
        string? Reason)
    {
        public bool Succeeded => !Hung &&
                                 Failed.Count == 0 &&
                                 TimedOut.Count == 0 &&
                                 (HadResults || ExitCode == 0) &&
                                 (Passed.Count > 0 || HadResults);
    }

    public IsolateRunner(string[] baseTestArgs, int timeoutSeconds = 30, string? initialFilter = null, int? maxParallelBatches = null)
        : this(baseTestArgs, new TimeoutStrategy(TimeoutMode.Fixed, timeoutSeconds), initialFilter, maxParallelBatches)
    {
    }

    public IsolateRunner(string[] baseTestArgs, TimeoutStrategy timeoutStrategy, string? initialFilter = null, int? maxParallelBatches = null)
    {
        _baseTestArgs = baseTestArgs;
        _timeoutStrategy = timeoutStrategy;
        _initialFilter = initialFilter;
        _maxParallelBatches = NormalizeParallelism(maxParallelBatches);
    }

    /// <summary>
    /// Gets the current timeout strategy.
    /// </summary>
    public TimeoutStrategy TimeoutStrategy => _timeoutStrategy;

    private static int NormalizeParallelism(int? maxParallelBatches)
    {
        if (maxParallelBatches.HasValue)
            return Math.Max(1, maxParallelBatches.Value);

        var envValue = Environment.GetEnvironmentVariable("TESTRUNNER_ISOLATE_PARALLEL");
        if (int.TryParse(envValue, out var parsed) && parsed > 1)
            return parsed;

        return 1;
    }

    public async Task<int> RunAsync(string? initialFilter = null)
    {
        var result = await RunWithResultAsync(initialFilter);
        return result.IsolatedHangingTests.Count == 0 && result.FailedBatches.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Runs isolation and returns detailed results.
    /// </summary>
    public async Task<IsolationResult> RunWithResultAsync(string? initialFilter = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var totalBatches = 0;
        var passedBatches = 0;

        // Use constructor filter if no parameter provided
        var filter = initialFilter ?? _initialFilter;

        Console.WriteLine("Isolating tests in smaller groups...");
        Console.WriteLine($"Timeout: {_timeoutStrategy.GetDescription()}");
        if (filter != null)
            Console.WriteLine($"Filter: {filter}");
        Console.WriteLine();

        Console.WriteLine("Discovering tests...");
        var allTests = await ListTestsAsync(filter);
        if (allTests.Count == 0)
        {
            Console.WriteLine("No tests found.");
            return new IsolationResult([], [], 0, 0, stopwatch.Elapsed);
        }
        Console.WriteLine($"Found {allTests.Count} tests.");
        Console.WriteLine();

        // Build and display test tree
        _testTree = new TestTree();
        _testTree.AddTests(allTests);
        Console.WriteLine($"Total tests in tree: {_testTree.Root.TotalTestCount}");
        Console.WriteLine("Test hierarchy:");
        _testTree.Render(maxDepth: 5);
        Console.WriteLine();

        // Build once up front so isolation runs can use --no-build
        if (NeedsDotnetTest())
        {
            var built = await EnsurePrebuiltAsync();
            if (!built)
            {
                Console.WriteLine("Prebuild failed. Aborting isolation.");
                return new IsolationResult([], ["Prebuild failed"], 0, 0, stopwatch.Elapsed);
            }
        }

        var batches = BuildTestBatches(_testTree.Root);
        totalBatches = batches.Count;

        Console.WriteLine($"Running {batches.Count} group(s) (<= {MaxTestsPerBatch} tests each)...");
        if (_maxParallelBatches > 1)
        {
            Console.WriteLine($"Parallel batches: {_maxParallelBatches}");
        }
        Console.WriteLine();

        var results = await RunInitialBatchesAsync(batches);

        var hangingGroups = results.Where(r => r.Hung).ToList();
        var failingGroups = results.Where(r => !r.Hung && !r.Succeeded).ToList();
        passedBatches = results.Count - hangingGroups.Count - failingGroups.Count;

        // Track failed batches
        foreach (var fg in failingGroups)
        {
            _failedBatches.Add(fg.Label);
        }

        Console.WriteLine("Initial isolation complete.");
        Console.WriteLine($"Batches: {results.Count}, Passed: {passedBatches}, Failed: {failingGroups.Count}, Hung: {hangingGroups.Count}");
        Console.WriteLine();

        // Recursively drill down into hanging groups to isolate individual tests
        if (hangingGroups.Count > 0)
        {
            Console.WriteLine("Drilling down into hanging groups to isolate specific tests...");
            Console.WriteLine();

            // Increment attempt counter for graduated timeouts during drilling
            _currentAttempt++;

            foreach (var hangingGroup in hangingGroups)
            {
                await DrillDownHangingBatchAsync(hangingGroup, 1);
            }
        }

        stopwatch.Stop();

        // Use ChartRenderer for the final summary
        ChartRenderer.RenderIsolationSummary(
            _isolatedHangingTests,
            _failedBatches,
            totalBatches + _completedBatches.Count, // Include drill-down batches
            passedBatches,
            stopwatch.Elapsed);

        return new IsolationResult(
            _isolatedHangingTests,
            _failedBatches,
            totalBatches + _completedBatches.Count,
            passedBatches,
            stopwatch.Elapsed);
    }

    private Task<List<BatchRunResult>> RunInitialBatchesAsync(List<TestBatch> batches)
    {
        return _maxParallelBatches > 1
            ? RunBatchesInParallelAsync(batches)
            : RunBatchesSequentialAsync(batches);
    }

    private async Task<List<BatchRunResult>> RunBatchesSequentialAsync(List<TestBatch> batches)
    {
        var results = new List<BatchRunResult>();
        var index = 1;

        foreach (var batch in batches)
        {
            Console.WriteLine($"[{index}/{batches.Count}] {batch.Label} ({batch.Tests.Count} tests)");

            var result = await RunBatchAsync(batch, line => Console.WriteLine($"    {line}"));
            results.Add(result);

            WriteBatchResult(batch, result, index, batches.Count, includeHeader: false);
            index++;
        }

        return results;
    }

    private async Task<List<BatchRunResult>> RunBatchesInParallelAsync(List<TestBatch> batches)
    {
        var results = new BatchRunResult?[batches.Count];
        using var semaphore = new SemaphoreSlim(_maxParallelBatches);

        var tasks = batches.Select(async (batch, index) =>
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await RunBatchAsync(batch);
                results[index] = result;
                WriteBatchResult(batch, result, index + 1, batches.Count, includeHeader: true);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);

        return results.Select(r => r ?? throw new InvalidOperationException("Batch execution did not return a result.")).ToList();
    }

    private void WriteBatchResult(TestBatch batch, BatchRunResult result, int index, int total, bool includeHeader)
    {
        var lines = new List<string>();

        if (includeHeader)
            lines.Add($"[{index}/{total}] {batch.Label} ({batch.Tests.Count} tests)");

        if (result.Succeeded)
        {
            lines.Add($"  ✓ Passed (exit {result.ExitCode}, results: {result.Passed.Count})");
        }
        else if (result.Hung)
        {
            lines.Add($"  ⏱ Hang/timeout suspected (timed out: {result.TimedOut.Count})");
            foreach (var timedOut in result.TimedOut)
            {
                lines.Add($"    ⏱ {timedOut}");
            }
        }
        else
        {
            var reason = result.Reason ?? $"exit code {result.ExitCode}";
            lines.Add($"  ✗ Failed ({result.Failed.Count} failed, {result.Passed.Count} passed, {reason})");
            foreach (var failed in result.Failed.Take(3))
            {
                lines.Add($"    ✗ {failed}");
            }
            if (result.Failed.Count > 3)
            {
                lines.Add($"    ...and {result.Failed.Count - 3} more");
            }
            if (!result.HadResults)
            {
                lines.Add("    ⚠️ No TRX results captured for this batch (likely filter mismatch).");
            }
        }

        lines.Add(string.Empty);
        WriteLines(lines);
    }

    private void WriteLines(IEnumerable<string> lines)
    {
        lock (_consoleLock)
        {
            foreach (var line in lines)
            {
                Console.WriteLine(line);
            }
        }
    }

    /// <summary>
    /// Recursively drills down into a hanging batch to isolate individual hanging tests.
    /// </summary>
    private async Task DrillDownHangingBatchAsync(BatchRunResult hangingBatch, int depth)
    {
        var indent = new string(' ', depth * 2);

        if (depth > MaxRecursionDepth)
        {
            Console.WriteLine($"{indent}⚠ Max recursion depth reached for {hangingBatch.Label}");
            // Add all tests from this batch as hanging
            foreach (var test in hangingBatch.TimedOut)
            {
                if (!_isolatedHangingTests.Contains(test))
                    _isolatedHangingTests.Add(test);
            }
            return;
        }

        // If we already have timed out tests identified, we're done drilling
        if (hangingBatch.TimedOut.Count > 0 && hangingBatch.TotalTests <= 10)
        {
            Console.WriteLine($"{indent}Found {hangingBatch.TimedOut.Count} hanging test(s) in {hangingBatch.Label}:");
            foreach (var test in hangingBatch.TimedOut)
            {
                Console.WriteLine($"{indent}  ⏱ {test}");
                if (!_isolatedHangingTests.Contains(test))
                    _isolatedHangingTests.Add(test);
            }
            return;
        }

        // Find the node(s) in the tree that correspond to this batch
        var nodesToDrill = FindNodesForBatch(hangingBatch);

        if (nodesToDrill.Count == 0)
        {
            Console.WriteLine($"{indent}⚠ Could not find tree nodes for {hangingBatch.Label}");
            return;
        }

        Console.WriteLine($"{indent}Drilling into {hangingBatch.Label} ({hangingBatch.TotalTests} tests)...");

        // Get child branches to test
        var childBatches = BuildChildBatches(nodesToDrill);

        if (childBatches.Count == 0)
        {
            // We're at leaf level - these are the hanging tests
            Console.WriteLine($"{indent}  At leaf level - isolating individual tests");
            foreach (var test in hangingBatch.TimedOut)
            {
                if (!_isolatedHangingTests.Contains(test))
                    _isolatedHangingTests.Add(test);
            }
            return;
        }

        if (childBatches.Count == 1 && childBatches[0].Tests.Count == hangingBatch.TotalTests)
        {
            // Only one child and it has all the tests - we need to drill deeper
            var singleNode = nodesToDrill[0];
            if (singleNode.Children.Count > 0)
            {
                // Get children of children
                var deeperBatches = singleNode.Children
                    .Select(c => new TestBatch(
                        GetNodeLabel(c),
                        TestTree.GetAllTests(c),
                        [GetFilterForNode(c)!]))
                    .Where(b => b.Tests.Count > 0)
                    .ToList();

                if (deeperBatches.Count > 0)
                {
                    childBatches = deeperBatches;
                }
            }
        }

        Console.WriteLine($"{indent}  Running {childBatches.Count} child batch(es)...");

        foreach (var childBatch in childBatches)
        {
            if (_completedBatches.Contains(childBatch.Label))
            {
                Console.WriteLine($"{indent}    Skipping {childBatch.Label} (already processed)");
                continue;
            }

            Console.WriteLine($"{indent}    Testing {childBatch.Label} ({childBatch.Tests.Count} tests)...");

            var result = await RunBatchAsync(childBatch, line => Console.WriteLine($"    {line}"));
            _completedBatches.Add(childBatch.Label);

            if (result.Succeeded)
            {
                Console.WriteLine($"{indent}    ✓ {childBatch.Label} passed - branch is clean");
                continue;
            }

            if (result.Hung)
            {
                if (childBatch.Tests.Count == 1)
                {
                    // Single test that hangs - we found it!
                    var hangingTest = childBatch.Tests[0];
                    Console.WriteLine($"{indent}    ⏱ ISOLATED: {hangingTest}");
                    if (!_isolatedHangingTests.Contains(hangingTest))
                        _isolatedHangingTests.Add(hangingTest);
                }
                else if (result.TimedOut.Count == 1)
                {
                    // dotnet test told us exactly which test timed out
                    var hangingTest = result.TimedOut.First();
                    Console.WriteLine($"{indent}    ⏱ ISOLATED: {hangingTest}");
                    if (!_isolatedHangingTests.Contains(hangingTest))
                        _isolatedHangingTests.Add(hangingTest);
                }
                else
                {
                    // Need to drill deeper
                    await DrillDownHangingBatchAsync(result, depth + 1);
                }
            }
            else
            {
                // Failed but not hanging - report but don't drill
                Console.WriteLine($"{indent}    ✗ {childBatch.Label} failed ({result.Failed.Count} failures)");
            }
        }
    }

    /// <summary>
    /// Finds tree nodes that correspond to a batch's filter prefixes.
    /// </summary>
    private List<TestTreeNode> FindNodesForBatch(BatchRunResult batch)
    {
        var nodes = new List<TestTreeNode>();

        foreach (var prefix in batch.TimedOut.Count > 0
            ? batch.TimedOut.Select(GetTestPrefix).Distinct()
            : [batch.Label])
        {
            var node = _testTree!.FindNodeByPath(prefix);
            if (node != null && !nodes.Contains(node))
            {
                nodes.Add(node);
            }
        }

        // Fallback: try to find by label
        if (nodes.Count == 0)
        {
            var node = _testTree!.FindNodeByPath(batch.Label);
            if (node != null)
                nodes.Add(node);
        }

        return nodes;
    }

    private static string GetTestPrefix(string testName)
    {
        // Get the namespace.class portion of a test name
        var lastDot = testName.LastIndexOf('.');
        return lastDot > 0 ? testName[..lastDot] : testName;
    }

    /// <summary>
    /// Builds batches from the children of the given nodes.
    /// </summary>
    private static List<TestBatch> BuildChildBatches(List<TestTreeNode> nodes)
    {
        var batches = new List<TestBatch>();

        foreach (var node in nodes)
        {
            if (node.Children.Count == 0)
            {
                // This is a leaf node - create a batch for each test
                foreach (var test in node.Tests)
                {
                    batches.Add(new TestBatch(
                        test,
                        [test],
                        [test]));
                }
            }
            else
            {
                // Create a batch for each child branch
                foreach (var child in node.Children.OrderBy(c => c.Name))
                {
                    var tests = TestTree.GetAllTests(child);
                    if (tests.Count > 0)
                    {
                        var filterPrefix = GetFilterForNode(child);
                        batches.Add(new TestBatch(
                            GetNodeLabel(child),
                            tests,
                            filterPrefix != null ? [filterPrefix] : []));
                    }
                }
            }
        }

        return batches;
    }

    private List<TestBatch> BuildTestBatches(TestTreeNode root)
    {
        // Find maximal nodes under limit
        var eligibleNodes = new List<TestTreeNode>();
        CollectEligibleNodes(root, parentOverLimit: true, eligibleNodes);

        // Fallback: if nothing qualified (e.g., every leaf > Max), use smallest leaves
        if (eligibleNodes.Count == 0)
        {
            eligibleNodes.AddRange(GetLeaves(root).OrderBy(n => n.TotalTestCount));
        }

        // Combine prefixes until total tests <= MaxTestsPerBatch
        var batches = new List<TestBatch>();
        var currentNodes = new List<TestTreeNode>();
        var currentTests = new List<string>();
        var currentCount = 0;

        foreach (var node in eligibleNodes)
        {
            var nodeTests = TestTree.GetAllTests(node);
            if (currentCount + nodeTests.Count > MaxTestsPerBatch && currentNodes.Count > 0)
            {
                batches.Add(BuildCombinedBatch(currentNodes, currentTests));
                currentNodes.Clear();
                currentTests.Clear();
                currentCount = 0;
            }

            currentNodes.Add(node);
            currentTests.AddRange(nodeTests);
            currentCount += nodeTests.Count;
        }

        if (currentNodes.Count > 0)
        {
            batches.Add(BuildCombinedBatch(currentNodes, currentTests));
        }

        return batches;
    }

    private static TestBatch BuildCombinedBatch(List<TestTreeNode> nodes, List<string> tests)
    {
        var prefixes = nodes
            .Select(GetFilterForNode)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var label = nodes.Count == 1
            ? GetNodeLabel(nodes[0])
            : $"{nodes.Count} branches ({tests.Count} tests)";

        return new TestBatch(label, new List<string>(tests), prefixes);
    }

    private void CollectEligibleNodes(TestTreeNode node, bool parentOverLimit, List<TestTreeNode> eligible)
    {
        var overLimit = node.TotalTestCount > MaxTestsPerBatch;

        // Select node if it fits and parent was too big (maximal under-the-limit node)
        if (!overLimit && parentOverLimit)
        {
            eligible.Add(node);
            return;
        }

        foreach (var child in node.Children.OrderBy(c => c.Name))
        {
            CollectEligibleNodes(child, overLimit, eligible);
        }
    }

    private static IEnumerable<TestTreeNode> GetLeaves(TestTreeNode node)
    {
        if (node.Children.Count == 0)
            yield return node;
        else
        {
            foreach (var child in node.Children)
            {
                foreach (var leaf in GetLeaves(child))
                    yield return leaf;
            }
        }
    }

    private static string GetNodeLabel(TestTreeNode node)
    {
        return string.IsNullOrWhiteSpace(node.FullPath)
            ? "All Tests"
            : node.FullPath;
    }

    private static string? GetFilterForNode(TestTreeNode node)
    {
        return string.IsNullOrWhiteSpace(node.FullPath) ? null : node.FullPath;
    }

    private async Task<BatchRunResult> RunBatchAsync(TestBatch batch, Action<string>? progressLogger = null)
    {
        if (batch.Tests.Count == 0)
        {
            return new BatchRunResult(batch.Label, 0, [], [], [], 0, false, false, "No tests to run");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"testrunner_isolate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var args = _baseTestArgs.ToList();
            RemoveFilterArgs(args);
            EnsureReleaseConfiguration(args);
            AddNoBuild(args);

            args.Add("--logger");
            args.Add("trx");
            args.Add("--results-directory");
            args.Add(tempDir);

            // Calculate timeout based on strategy
            var perTestTimeout = _timeoutStrategy.GetTimeout(_currentAttempt);
            var batchTimeout = _timeoutStrategy.GetBatchTimeout(batch.Tests.Count, _currentAttempt);

            if (perTestTimeout > 0)
            {
                args.Add("--blame-hang");
                args.Add("--blame-hang-timeout");
                args.Add($"{perTestTimeout}s");
            }

            var filter = BuildFilter(batch);
            if (!string.IsNullOrWhiteSpace(filter))
            {
                args.Add("--filter");
                args.Add(filter);
            }

            var batchTimeoutSeconds = batchTimeout > 0
                ? Math.Max(batchTimeout, 120) // at least 2 minutes
                : 180; // default guardrail when hang detection disabled

            var idleTimeoutSeconds = Math.Max(batchTimeoutSeconds / 2, 90); // kill if no output for too long

            Action<string>? filteredLogger = null;
            if (progressLogger != null)
            {
                filteredLogger = line =>
                {
                    if (IsInterestingOutput(line))
                        progressLogger(line);
                };
            }

            (int exitCode, bool killedByGuard, string? guardReason) = await RunDotnetProcessAsync(
                args,
                batchTimeoutSeconds,
                idleTimeoutSeconds,
                filteredLogger);

            var results = Directory.GetFiles(tempDir, "*.trx", SearchOption.AllDirectories)
                .Select(TrxParser.ParseTrxFile)
                .Where(r => r != null)
                .ToList();

            string? reason = null;
            var passed = new HashSet<string>(results.SelectMany(r => r!.PassedTests));
            var failed = new HashSet<string>(results.SelectMany(r => r!.FailedTests));
            var timedOut = new HashSet<string>(results.SelectMany(r => r!.TimedOutTests));

            var hung = timedOut.Count > 0 || DetectHangArtifacts(tempDir);
            if (killedByGuard)
            {
                hung = true;
                if (reason == null)
                    reason = guardReason ?? "Batch terminated by guard";
            }

            var hadResults = results.Count > 0;

            if (!hadResults)
            {
                reason = exitCode != 0
                    ? $"dotnet test exited {exitCode} with no results (filter mismatch?)"
                    : "dotnet test produced no results";
            }
            else if (exitCode != 0 && failed.Count == 0 && timedOut.Count == 0)
            {
                reason = $"exit code {exitCode} despite no failed/hanging tests";
            }

            return new BatchRunResult(batch.Label, batch.Tests.Count, passed, failed, timedOut, exitCode, hung, hadResults, reason);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static void RemoveFilterArgs(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--filter", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    args.RemoveAt(i + 1);
                }

                args.RemoveAt(i);
                i--;
            }
            else if (arg.StartsWith("--filter", StringComparison.OrdinalIgnoreCase))
            {
                args.RemoveAt(i);
                i--;
            }
        }
    }

    private static void AddNoBuild(List<string> args)
    {
        if (args.Any(a => a.Equals("--no-build", StringComparison.OrdinalIgnoreCase)))
            return;

        args.Add("--no-build");
    }

    private static string? BuildFilter(TestBatch batch)
    {
        if (batch.FilterPrefixes.Count == 0)
            return null;

        var clauses = batch.FilterPrefixes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => $"FullyQualifiedName~{EscapeFilterValue(p!)}")
            .ToList();

        if (clauses.Count == 0)
            return null;

        return string.Join("|", clauses);
    }

    private static bool IsInterestingOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var lower = line.ToLowerInvariant();
        return lower.Contains("passed") ||
               lower.Contains("failed") ||
               lower.Contains("skipped") ||
               lower.Contains("starting test execution") ||
               lower.Contains("total tests") ||
               lower.StartsWith("test run for", StringComparison.OrdinalIgnoreCase) ||
               lower.Contains("discovering") ||
               lower.Contains("results file") ||
               lower.Contains("starting test");
    }

    private bool NeedsDotnetTest()
    {
        if (_baseTestArgs.Length == 0)
            return false;

        var first = _baseTestArgs[0].ToLowerInvariant();
        if (first != "dotnet")
            return false;

        return _baseTestArgs.Skip(1).Any(a => a.Equals("test", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> EnsurePrebuiltAsync()
    {
        try
        {
            var buildArgs = _baseTestArgs.ToList();
            RemoveFilterArgs(buildArgs);
            CleanBuildOnlyArgs(buildArgs);
            EnsureReleaseConfiguration(buildArgs);

            // Replace the first "test" with "build"
            for (var i = 0; i < buildArgs.Count; i++)
            {
                if (buildArgs[i].Equals("test", StringComparison.OrdinalIgnoreCase))
                {
                    buildArgs[i] = "build";
                    break;
                }
            }

            // Remove dotnet if present; RunDotnetProcessAsync expects args without leading dotnet
            if (buildArgs.Count > 0 && buildArgs[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                buildArgs = buildArgs.Skip(1).ToList();
            }

            Console.WriteLine("Prebuilding tests once (Release)...");
            var (exitCode, _, _) = await RunDotnetProcessAsync(buildArgs);
            if (exitCode != 0)
            {
                Console.WriteLine($"Prebuild failed with exit code {exitCode}");
                return false;
            }

            Console.WriteLine("Prebuild completed.");
            Console.WriteLine();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanBuildOnlyArgs(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals("--no-build", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--blame-hang", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--logger", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-l", StringComparison.OrdinalIgnoreCase))
            {
                args.RemoveAt(i);
                i--;
                continue;
            }

            if (arg.Equals("--results-directory", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--blame-hang-timeout", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    args.RemoveAt(i + 1);
                }
                args.RemoveAt(i);
                i--;
            }
        }
    }

    private static void EnsureReleaseConfiguration(List<string> args)
    {
        var hasConfig = args.Any(a =>
            a.Equals("-c", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--configuration", StringComparison.OrdinalIgnoreCase));

        if (hasConfig)
            return;

        args.Add("--configuration");
        args.Add("Release");
    }

    private static bool DetectHangArtifacts(string tempDir)
    {
        var sequenceFiles = Directory.GetFiles(tempDir, "Sequence_*.xml", SearchOption.AllDirectories);
        if (sequenceFiles.Length > 0)
        {
            return true;
        }

        var hangDumps = Directory.GetFiles(tempDir, "*_hangdump*", SearchOption.AllDirectories);
        return hangDumps.Length > 0;
    }

    private static string EscapeFilterValue(string value)
    {
        return value.Replace("(", "\\(").Replace(")", "\\)");
    }

    private async Task<List<string>> ListTestsAsync(string? filter)
    {
        var args = _baseTestArgs.ToList();
        args.Add("--list-tests");

        // Don't pass filter to dotnet test --list-tests (it ignores it anyway)
        // We'll filter locally instead

        var output = await RunDotnetAsync(args, captureOutput: true, timeout: 120);

        // Parse test names from output
        var tests = new List<string>();
        var inTestList = false;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("The following Tests are available:"))
            {
                inTestList = true;
                continue;
            }
            if (inTestList && !string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("Test run for"))
            {
                tests.Add(trimmed);
            }
        }

        // Filter locally if a filter is specified
        if (filter != null)
        {
            tests = tests.Where(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return tests;
    }

    private async Task<string> RunDotnetAsync(List<string> args, bool captureOutput, int timeout)
    {
        var executable = "dotnet";
        var commandArgs = args.ToArray();

        if (commandArgs.Length > 0 && commandArgs[0] == "dotnet")
            commandArgs = commandArgs.Skip(1).ToArray();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in commandArgs)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        var output = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
        }

        return output.ToString();
    }

    private async Task<(int ExitCode, bool KilledByGuard, string? GuardReason)> RunDotnetProcessAsync(
        List<string> args,
        int? killAfterSeconds = null,
        int? idleKillSeconds = null,
        Action<string>? progressLogger = null,
        int heartbeatSeconds = 30)
    {
        var executable = "dotnet";
        var commandArgs = args.ToArray();

        if (commandArgs.Length > 0 && commandArgs[0] == "dotnet")
            commandArgs = commandArgs.Skip(1).ToArray();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in commandArgs)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };

        var lastOutput = DateTime.UtcNow;
        process.OutputDataReceived += (_, e) =>
        {
            // Suppress console noise but track liveness
            if (e.Data != null)
            {
                progressLogger?.Invoke(e.Data);
                lastOutput = DateTime.UtcNow;
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                progressLogger?.Invoke(e.Data);
                lastOutput = DateTime.UtcNow;
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var killedByGuard = false;
        string? guardReason = null;
        var overallStopwatch = Stopwatch.StartNew();
        var lastHeartbeat = DateTime.UtcNow;

        while (!process.HasExited)
        {
            await Task.Delay(500);

            if (!killAfterSeconds.HasValue && !idleKillSeconds.HasValue)
                continue;

            if (killAfterSeconds.HasValue && overallStopwatch.Elapsed.TotalSeconds > killAfterSeconds.Value)
            {
                guardReason = $"Batch exceeded {killAfterSeconds.Value}s and was terminated";
                break;
            }

            if (idleKillSeconds.HasValue &&
                (DateTime.UtcNow - lastOutput).TotalSeconds > idleKillSeconds.Value)
            {
                guardReason = $"No test output for {idleKillSeconds.Value}s; batch terminated";
                break;
            }

            if (progressLogger != null &&
                heartbeatSeconds > 0 &&
                (DateTime.UtcNow - lastHeartbeat).TotalSeconds > heartbeatSeconds)
            {
                progressLogger($"... still running ({overallStopwatch.Elapsed:mm\\:ss}), last output {(DateTime.UtcNow - lastOutput).TotalSeconds:F0}s ago");
                lastHeartbeat = DateTime.UtcNow;
            }
        }

        if (guardReason != null && !process.HasExited)
        {
            try { process.Kill(true); } catch { }
            killedByGuard = true;
        }
        else
        {
            await process.WaitForExitAsync();
        }

        return (process.ExitCode, killedByGuard, guardReason);
    }
}
