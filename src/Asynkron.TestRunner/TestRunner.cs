using System.Diagnostics;
using Asynkron.TestRunner.Models;

namespace Asynkron.TestRunner;

public class TestRunner
{
    private const int DefaultTimeoutSeconds = 20;

    private readonly ResultStore _store;
    private readonly int _timeoutSeconds;
    private readonly string? _filter;
    private readonly bool _quiet;

    public TestRunner(ResultStore store, int? timeoutSeconds = null, string? filter = null, bool quiet = false)
    {
        _store = store;
        _timeoutSeconds = timeoutSeconds ?? DefaultTimeoutSeconds;
        _filter = filter;
        _quiet = quiet;
    }

    public async Task<int> RunTestsAsync(string[] args)
    {
        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var resultsDir = Path.Combine(_store.StoreFolder, runId);
        Directory.CreateDirectory(resultsDir);

        // In quiet mode, discover tests first and show the tree
        if (_quiet)
        {
            Console.WriteLine("Discovering tests...");
            var tests = await DiscoverTestsAsync(args, _filter);
            if (tests.Count > 0)
            {
                var tree = new TestTree();
                tree.AddTests(tests);
                Console.WriteLine($"Found {tests.Count} tests");
                Console.WriteLine();
                Console.WriteLine("Test hierarchy:");
                tree.Render(maxDepth: 5);
                Console.WriteLine();
            }
            Console.WriteLine("Running tests...");
        }

        // Build the command with TRX logger and blame-hang injected
        var processArgs = BuildArgs(args, resultsDir, _timeoutSeconds);

        // Find the executable (first arg after --)
        var executable = "dotnet";
        var commandArgs = processArgs;

        if (processArgs.Length > 0 && processArgs[0] == "dotnet")
        {
            commandArgs = processArgs.Skip(1).ToArray();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (!_quiet)
        {
            Console.WriteLine($"Running: {executable} {string.Join(" ", commandArgs)}");
            Console.WriteLine();
        }

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null && !_quiet) Console.WriteLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null && !_quiet) Console.Error.WriteLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        // Get previous run for comparison before saving new result
        var previousRun = _store.GetRecentRuns(1).FirstOrDefault();

        // Parse results from all TRX files in the results directory
        var result = TrxParser.ParseFromDirectory(resultsDir);
        if (result != null)
        {
            result.Id = runId;
            result.Timestamp = DateTime.Now;
            _store.SaveResult(result);

            Console.WriteLine();
            ChartRenderer.RenderSingleResult(result, previousRun);

            // Show history chart
            var history = _store.GetRecentRuns(10);
            if (history.Count > 1)
            {
                ChartRenderer.RenderHistory(history);
            }
        }
        else
        {
            Console.WriteLine("Warning: No TRX results found. Was the test run successful?");
        }

        // Output timeout info for AI agents and users
        Console.WriteLine($"[testrunner] Per-test timeout: {_timeoutSeconds}s (use --timeout <seconds> to change)");

        // If hang detected (blame-hang triggered), automatically isolate to find the culprit
        if (process.ExitCode != 0 && _timeoutSeconds > 0 && result != null)
        {
            // Check if the hang occurred (blame-hang would cause exit without all tests completing)
            var expectedHang = DetectHang(result, resultsDir);
            if (expectedHang)
            {
                Console.WriteLine();
                Console.WriteLine("════════════════════════════════════════════════════════════════");
                Console.WriteLine("HANG DETECTED - Automatically isolating to find hanging test(s)");
                Console.WriteLine("════════════════════════════════════════════════════════════════");
                Console.WriteLine();

                // Extract base test args (remove our injected options)
                var baseArgs = ExtractBaseTestArgs(args);
                var isolateRunner = new IsolateRunner(baseArgs, _timeoutSeconds, _filter);
                await isolateRunner.RunAsync(_filter);
            }
        }

        return process.ExitCode;
    }

    private static bool DetectHang(TestRunResult result, string resultsDir)
    {
        // Look for blame-hang sequence file which indicates a hang was detected
        var sequenceFiles = Directory.GetFiles(resultsDir, "Sequence_*.xml", SearchOption.AllDirectories);
        if (sequenceFiles.Length > 0)
            return true;

        // Also check for hang dump files
        var hangDumps = Directory.GetFiles(resultsDir, "*_hangdump*", SearchOption.AllDirectories);
        return hangDumps.Length > 0;
    }

    private static string[] ExtractBaseTestArgs(string[] args)
    {
        // Remove our injected args (--logger, --results-directory, --blame-hang, etc.)
        var result = new List<string>();
        var skipNext = false;

        foreach (var arg in args)
        {
            if (skipNext)
            {
                skipNext = false;
                continue;
            }

            if (arg.StartsWith("--logger", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--results-directory", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("-r", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--blame-hang", StringComparison.OrdinalIgnoreCase))
            {
                // Skip this arg and potentially its value
                if (arg.Equals("--logger", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--results-directory", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-r", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--blame-hang-timeout", StringComparison.OrdinalIgnoreCase))
                {
                    skipNext = true;
                }
                continue;
            }

            result.Add(arg);
        }

        return result.ToArray();
    }

    private static string[] BuildArgs(string[] args, string resultsDir, int timeoutSeconds)
    {
        var argsList = args.ToList();

        // Check if logger is already specified
        var hasLogger = argsList.Any(a =>
            a.StartsWith("--logger", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("-l", StringComparison.OrdinalIgnoreCase));

        if (!hasLogger)
        {
            // Inject TRX logger
            argsList.Add("--logger");
            argsList.Add("trx");
        }

        // Always set results directory to our folder
        var hasResultsDir = argsList.Any(a =>
            a.StartsWith("--results-directory", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("-r", StringComparison.OrdinalIgnoreCase));

        if (!hasResultsDir)
        {
            argsList.Add("--results-directory");
            argsList.Add(resultsDir);
        }

        // Add blame-hang for per-test timeout (unless already specified)
        var hasBlameHang = argsList.Any(a =>
            a.StartsWith("--blame-hang", StringComparison.OrdinalIgnoreCase));

        if (!hasBlameHang && timeoutSeconds > 0)
        {
            argsList.Add("--blame-hang");
            argsList.Add("--blame-hang-timeout");
            argsList.Add($"{timeoutSeconds}s");
        }

        return argsList.ToArray();
    }

    private async Task<List<string>> DiscoverTestsAsync(string[] args, string? filter)
    {
        // Check if we have DLL files in the args (vstest mode)
        var dllFiles = args
            .Where(arg => arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(arg))
            .ToList();

        if (dllFiles.Count > 0)
        {
            // Use structured discovery with vstest
            var assemblies = await TestDiscovery.DiscoverTestsAsync(dllFiles);
            var discoveredTests = new List<string>();

            foreach (var assembly in assemblies)
            {
                foreach (var ns in assembly.Namespaces)
                {
                    foreach (var cls in ns.Classes)
                    {
                        foreach (var method in cls.Methods)
                        {
                            // Add the fully qualified name (namespace.class.method)
                            discoveredTests.Add(method.FullyQualifiedName);
                        }
                    }
                }
            }

            // Filter if specified
            if (filter != null)
            {
                discoveredTests = discoveredTests.Where(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return discoveredTests;
        }

        // Fall back to dotnet test --list-tests
        var listArgs = args.ToList();
        listArgs.Add("--list-tests");

        var executable = "dotnet";
        var commandArgs = listArgs.ToArray();

        if (commandArgs.Length > 0 && commandArgs[0] == "dotnet")
        {
            commandArgs = commandArgs.Skip(1).ToArray();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var output = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
        }

        // Parse test names from output
        var tests = new List<string>();
        var inTestList = false;

        foreach (var line in output.ToString().Split('\n'))
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
}
