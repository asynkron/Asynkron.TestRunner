using System.Diagnostics;
using Spectre.Console;

namespace Asynkron.TestRunner;

public class IsolateRunner
{
    private const int MaxTestsPerBatch = 5000;

    private readonly int _timeoutSeconds;
    private readonly string[] _baseTestArgs;
    private readonly string? _initialFilter;
    private TestTree? _testTree;

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

    public IsolateRunner(string[] baseTestArgs, int timeoutSeconds = 30, string? initialFilter = null)
    {
        _baseTestArgs = baseTestArgs;
        _timeoutSeconds = timeoutSeconds;
        _initialFilter = initialFilter;
    }

    public async Task<int> RunAsync(string? initialFilter = null)
    {
        // Use constructor filter if no parameter provided
        var filter = initialFilter ?? _initialFilter;

        Console.WriteLine("Isolating tests in smaller groups...");
        if (filter != null)
            Console.WriteLine($"Filter: {filter}");
        Console.WriteLine();

        Console.WriteLine("Discovering tests...");
        var allTests = await ListTestsAsync(filter);
        if (allTests.Count == 0)
        {
            Console.WriteLine("No tests found.");
            return 1;
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
                return 1;
            }
        }

        var batches = BuildTestBatches(_testTree.Root);

        Console.WriteLine($"Running {batches.Count} group(s) (<= {MaxTestsPerBatch} tests each)...");
        Console.WriteLine();

        var results = new List<BatchRunResult>();
        var index = 1;
        foreach (var batch in batches)
        {
            Console.WriteLine($"[{index}/{batches.Count}] {batch.Label} ({batch.Tests.Count} tests)");

            var result = await RunBatchAsync(batch);
            results.Add(result);

            if (result.Succeeded)
            {
                Console.WriteLine($"  ✓ Passed (exit {result.ExitCode}, results: {result.Passed.Count})");
            }
            else if (result.Hung)
            {
                Console.WriteLine($"  ⏱ Hang/timeout suspected (timed out: {result.TimedOut.Count})");
                foreach (var timedOut in result.TimedOut)
                {
                    Console.WriteLine($"    ⏱ {timedOut}");
                }
            }
            else
            {
                var reason = result.Reason ?? $"exit code {result.ExitCode}";
                Console.WriteLine($"  ✗ Failed ({result.Failed.Count} failed, {result.Passed.Count} passed, {reason})");
                foreach (var failed in result.Failed.Take(3))
                {
                    Console.WriteLine($"    ✗ {failed}");
                }
                if (result.Failed.Count > 3)
                {
                    Console.WriteLine($"    ...and {result.Failed.Count - 3} more");
                }
                if (!result.HadResults)
                {
                    Console.WriteLine("    ⚠️ No TRX results captured for this batch (likely filter mismatch).");
                }
            }

            Console.WriteLine();
            index++;
        }

        var hangingGroups = results.Where(r => r.Hung).ToList();
        var failingGroups = results.Where(r => !r.Hung && !r.Succeeded).ToList();
        var passedGroups = results.Count - hangingGroups.Count - failingGroups.Count;

        Console.WriteLine("Isolation complete.");
        Console.WriteLine($"Batches: {results.Count}, Passed: {passedGroups}, Failed: {failingGroups.Count}, Hung: {hangingGroups.Count}");

        if (hangingGroups.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Hanging groups:");
            foreach (var group in hangingGroups)
            {
                Console.WriteLine($"  ⏱ {group.Label}");
            }
        }

        if (failingGroups.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failing groups:");
            foreach (var group in failingGroups)
            {
                Console.WriteLine($"  ✗ {group.Label}");
            }
        }

        return hangingGroups.Count == 0 && failingGroups.Count == 0 ? 0 : 1;
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

    private async Task<BatchRunResult> RunBatchAsync(TestBatch batch)
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

            if (_timeoutSeconds > 0)
            {
                args.Add("--blame-hang");
                args.Add("--blame-hang-timeout");
                args.Add($"{_timeoutSeconds}s");
            }

            var filter = BuildFilter(batch);
            if (!string.IsNullOrWhiteSpace(filter))
            {
                args.Add("--filter");
                args.Add(filter);
            }

            var batchTimeoutSeconds = _timeoutSeconds > 0
                ? Math.Max(_timeoutSeconds * 2, 120) // 2x per-test timeout, at least 2 minutes
                : 180; // default guardrail when hang detection disabled

            var idleTimeoutSeconds = Math.Max(batchTimeoutSeconds / 2, 90); // kill if no output for too long

            (int exitCode, bool killedByGuard, string? guardReason) = await RunDotnetProcessAsync(
                args,
                batchTimeoutSeconds,
                idleTimeoutSeconds,
                line =>
                {
                    if (IsInterestingOutput(line))
                        Console.WriteLine($"    {line}");
                });

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
