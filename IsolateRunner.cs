using System.Diagnostics;

namespace Asynkron.TestRunner;

public class IsolateRunner
{
    private readonly int _timeoutSeconds;
    private readonly string[] _baseTestArgs;
    private readonly HashSet<string> _completedPrefixes = [];

    public IsolateRunner(string[] baseTestArgs, int timeoutSeconds = 30)
    {
        _baseTestArgs = baseTestArgs;
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<int> RunAsync(string? initialFilter = null)
    {
        Console.WriteLine("Isolating hanging test(s)...");
        Console.WriteLine();

        // Step 1: List all tests
        Console.WriteLine("Step 1: Discovering tests...");
        var allTests = await ListTestsAsync(initialFilter);
        if (allTests.Count == 0)
        {
            Console.WriteLine("No tests found.");
            return 1;
        }
        Console.WriteLine($"Found {allTests.Count} tests.");
        Console.WriteLine();

        // Step 2: Run all tests to see if there's a hang
        Console.WriteLine("Step 2: Running all tests to detect hang...");
        var (passed, hung) = await RunTestsAsync(initialFilter);

        if (!hung)
        {
            Console.WriteLine("All tests passed without hanging!");
            return 0;
        }

        Console.WriteLine($"Hang detected! {passed.Count} tests passed before hang.");
        Console.WriteLine();

        // Mark completed prefixes based on passed tests
        MarkCompletedPrefixes(allTests, passed);

        // Step 3: Find hanging test by drilling down
        Console.WriteLine("Step 3: Isolating hanging test(s)...");
        Console.WriteLine();

        var hangingTests = await IsolateHangingTestsAsync(allTests, passed, initialFilter);

        if (hangingTests.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.WriteLine("HANGING TEST(S) FOUND:");
            Console.WriteLine("═══════════════════════════════════════════════════════");
            foreach (var test in hangingTests)
            {
                Console.WriteLine($"  ⏱ {test}");
            }
            Console.WriteLine("═══════════════════════════════════════════════════════");
        }
        else
        {
            Console.WriteLine("Could not isolate the hanging test. It may be an ordering/interaction issue.");
        }

        return hangingTests.Count > 0 ? 0 : 1;
    }

    private async Task<List<string>> IsolateHangingTestsAsync(
        List<string> allTests,
        HashSet<string> passed,
        string? baseFilter)
    {
        var hangingTests = new List<string>();
        var remaining = allTests.Where(t => !passed.Contains(t)).ToList();

        if (remaining.Count == 0)
        {
            Console.WriteLine("All tests passed - hang may be in test cleanup/teardown.");
            return hangingTests;
        }

        // Group by top-level namespace
        var groups = GroupByNamespaceLevel(remaining, 0);

        foreach (var group in groups.OrderBy(g => g.Key))
        {
            if (IsGroupCompleted(group.Key))
            {
                Console.WriteLine($"  ✓ {group.Key} (already completed)");
                continue;
            }

            var result = await DrillDownAsync(group.Key, group.Value, 0);
            hangingTests.AddRange(result);
        }

        return hangingTests;
    }

    private async Task<List<string>> DrillDownAsync(string prefix, List<string> tests, int depth)
    {
        var indent = new string(' ', depth * 2);
        var hangingTests = new List<string>();

        // If only one test, that's our culprit
        if (tests.Count == 1)
        {
            Console.WriteLine($"{indent}→ Testing: {tests[0]}");
            var (_, singleHung) = await RunTestsAsync(tests[0]);
            if (singleHung)
            {
                Console.WriteLine($"{indent}  ⏱ HANGS: {tests[0]}");
                return [tests[0]];
            }
            else
            {
                Console.WriteLine($"{indent}  ✓ Passes in isolation");
                _completedPrefixes.Add(tests[0]);
                return [];
            }
        }

        Console.WriteLine($"{indent}→ Testing group: {prefix} ({tests.Count} tests)");

        // Run this group
        var (passed, hung) = await RunTestsAsync(prefix);

        if (!hung)
        {
            Console.WriteLine($"{indent}  ✓ Group passes in isolation");
            _completedPrefixes.Add(prefix);
            return [];
        }

        Console.WriteLine($"{indent}  ⏱ Group hangs! Drilling down...");

        // Get remaining tests in this group
        var remaining = tests.Where(t => !passed.Contains(t)).ToList();

        if (remaining.Count == 0)
        {
            Console.WriteLine($"{indent}  All tests passed but still hung - likely teardown issue in: {prefix}");
            return [prefix + " (teardown)"];
        }

        if (remaining.Count == tests.Count && tests.Count <= 3)
        {
            // No tests passed and only a few left - test each one
            foreach (var test in remaining)
            {
                var (_, testHung) = await RunTestsAsync(test);
                if (testHung)
                {
                    Console.WriteLine($"{indent}  ⏱ HANGS: {test}");
                    hangingTests.Add(test);
                }
                else
                {
                    Console.WriteLine($"{indent}  ✓ {test} passes in isolation");
                }
            }
            return hangingTests;
        }

        // Drill down to next namespace level
        var nextLevel = depth + 1;
        var subGroups = GroupByNamespaceLevel(remaining, nextLevel);

        if (subGroups.Count == 1 && subGroups.First().Value.Count == remaining.Count)
        {
            // Can't split further by namespace, split by count
            var half = remaining.Count / 2;
            var firstHalf = remaining.Take(half).ToList();
            var secondHalf = remaining.Skip(half).ToList();

            Console.WriteLine($"{indent}  Splitting {remaining.Count} tests into two halves...");

            // Test first half
            var firstResult = await TestSubsetAsync(firstHalf, depth + 1);
            hangingTests.AddRange(firstResult);

            // Test second half
            var secondResult = await TestSubsetAsync(secondHalf, depth + 1);
            hangingTests.AddRange(secondResult);
        }
        else
        {
            foreach (var subGroup in subGroups.OrderBy(g => g.Key))
            {
                if (IsGroupCompleted(subGroup.Key))
                    continue;

                var result = await DrillDownAsync(subGroup.Key, subGroup.Value, depth + 1);
                hangingTests.AddRange(result);
            }
        }

        return hangingTests;
    }

    private async Task<List<string>> TestSubsetAsync(List<string> tests, int depth)
    {
        var indent = new string(' ', depth * 2);
        var hangingTests = new List<string>();

        if (tests.Count == 0) return hangingTests;

        if (tests.Count == 1)
        {
            var (_, singleHung) = await RunTestsAsync(tests[0]);
            if (singleHung)
            {
                Console.WriteLine($"{indent}⏱ HANGS: {tests[0]}");
                hangingTests.Add(tests[0]);
            }
            return hangingTests;
        }

        // Build a filter for this subset
        var filter = string.Join("|", tests.Select(t => $"FullyQualifiedName={EscapeFilterValue(t)}"));

        // If filter is too long, fall back to running one by one
        if (filter.Length > 8000)
        {
            Console.WriteLine($"{indent}Filter too long, testing {tests.Count} tests one by one...");
            foreach (var test in tests)
            {
                var (_, testHung) = await RunTestsAsync(test);
                if (testHung)
                {
                    Console.WriteLine($"{indent}⏱ HANGS: {test}");
                    hangingTests.Add(test);
                }
            }
            return hangingTests;
        }

        var (passed, hung) = await RunTestsWithRawFilterAsync(filter);

        if (!hung)
        {
            return hangingTests;
        }

        // Hang in this subset, drill down further
        var remaining = tests.Where(t => !passed.Contains(t)).ToList();

        if (remaining.Count <= 3)
        {
            foreach (var test in remaining)
            {
                var (_, testHung) = await RunTestsAsync(test);
                if (testHung)
                {
                    Console.WriteLine($"{indent}⏱ HANGS: {test}");
                    hangingTests.Add(test);
                }
            }
        }
        else
        {
            var half = remaining.Count / 2;
            hangingTests.AddRange(await TestSubsetAsync(remaining.Take(half).ToList(), depth + 1));
            hangingTests.AddRange(await TestSubsetAsync(remaining.Skip(half).ToList(), depth + 1));
        }

        return hangingTests;
    }

    private static string EscapeFilterValue(string value)
    {
        // Escape special characters in filter values
        return value.Replace("(", "\\(").Replace(")", "\\)");
    }

    private Dictionary<string, List<string>> GroupByNamespaceLevel(List<string> tests, int level)
    {
        var groups = new Dictionary<string, List<string>>();

        foreach (var test in tests)
        {
            // Strip parameters from parameterized tests: "Namespace.Class.Method(params)" -> "Namespace.Class.Method"
            var baseName = GetTestBaseName(test);
            var parts = baseName.Split('.');
            var prefix = string.Join(".", parts.Take(Math.Min(level + 1, parts.Length)));

            if (!groups.ContainsKey(prefix))
                groups[prefix] = [];

            groups[prefix].Add(test);
        }

        return groups;
    }

    private static string GetTestBaseName(string testName)
    {
        // Strip parameters: "Namespace.Class.Method(param1, param2)" -> "Namespace.Class.Method"
        var parenIndex = testName.IndexOf('(');
        return parenIndex > 0 ? testName[..parenIndex] : testName;
    }

    private void MarkCompletedPrefixes(List<string> allTests, HashSet<string> passed)
    {
        // Group all tests by namespace prefixes at each level
        // Mark a prefix as complete if ALL tests under it passed

        var testsByPrefix = new Dictionary<string, HashSet<string>>();
        var passedByPrefix = new Dictionary<string, HashSet<string>>();

        foreach (var test in allTests)
        {
            var baseName = GetTestBaseName(test);
            var parts = baseName.Split('.');
            for (var i = 1; i <= parts.Length; i++)
            {
                var prefix = string.Join(".", parts.Take(i));
                if (!testsByPrefix.ContainsKey(prefix))
                    testsByPrefix[prefix] = [];
                testsByPrefix[prefix].Add(test);
            }
        }

        foreach (var test in passed)
        {
            var baseName = GetTestBaseName(test);
            var parts = baseName.Split('.');
            for (var i = 1; i <= parts.Length; i++)
            {
                var prefix = string.Join(".", parts.Take(i));
                if (!passedByPrefix.ContainsKey(prefix))
                    passedByPrefix[prefix] = [];
                passedByPrefix[prefix].Add(test);
            }
        }

        foreach (var (prefix, allInPrefix) in testsByPrefix)
        {
            if (passedByPrefix.TryGetValue(prefix, out var passedInPrefix))
            {
                if (passedInPrefix.Count == allInPrefix.Count)
                {
                    _completedPrefixes.Add(prefix);
                }
            }
        }
    }

    private bool IsGroupCompleted(string prefix)
    {
        return _completedPrefixes.Contains(prefix);
    }

    private async Task<List<string>> ListTestsAsync(string? filter)
    {
        var args = _baseTestArgs.ToList();
        args.Add("--list-tests");

        if (filter != null)
        {
            args.Add("--filter");
            args.Add($"FullyQualifiedName~{filter}");
        }

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

        return tests;
    }

    private async Task<(HashSet<string> Passed, bool Hung)> RunTestsAsync(string? filter)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"testrunner_isolate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var args = _baseTestArgs.ToList();
            args.Add("--logger");
            args.Add("trx");
            args.Add("--results-directory");
            args.Add(tempDir);
            args.Add("--blame-hang");
            args.Add("--blame-hang-timeout");
            args.Add($"{_timeoutSeconds}s");

            if (filter != null)
            {
                args.Add("--filter");
                args.Add($"FullyQualifiedName~{filter}");
            }

            var exitCode = await RunDotnetProcessAsync(args);
            var hung = exitCode != 0;

            // Parse TRX for passed tests
            var passed = new HashSet<string>();
            var trxFiles = Directory.GetFiles(tempDir, "*.trx", SearchOption.AllDirectories);
            foreach (var trx in trxFiles)
            {
                var result = TrxParser.ParseTrxFile(trx);
                if (result != null)
                {
                    foreach (var test in result.PassedTests)
                        passed.Add(test);
                }
            }

            return (passed, hung);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private async Task<(HashSet<string> Passed, bool Hung)> RunTestsWithRawFilterAsync(string rawFilter)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"testrunner_isolate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var args = _baseTestArgs.ToList();
            args.Add("--logger");
            args.Add("trx");
            args.Add("--results-directory");
            args.Add(tempDir);
            args.Add("--blame-hang");
            args.Add("--blame-hang-timeout");
            args.Add($"{_timeoutSeconds}s");
            args.Add("--filter");
            args.Add(rawFilter);

            var exitCode = await RunDotnetProcessAsync(args);
            var hung = exitCode != 0;

            var passed = new HashSet<string>();
            var trxFiles = Directory.GetFiles(tempDir, "*.trx", SearchOption.AllDirectories);
            foreach (var trx in trxFiles)
            {
                var result = TrxParser.ParseTrxFile(trx);
                if (result != null)
                {
                    foreach (var test in result.PassedTests)
                        passed.Add(test);
                }
            }

            return (passed, hung);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
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

    private async Task<int> RunDotnetProcessAsync(List<string> args)
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

        // Suppress output during isolation runs
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
