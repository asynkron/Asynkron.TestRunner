using System.Diagnostics;
using Asynkron.TestRunner.Models;

namespace Asynkron.TestRunner;

public class TestRunner
{
    private const int DefaultTimeoutSeconds = 20;

    private readonly ResultStore _store;
    private readonly int _timeoutSeconds;
    private readonly string? _filter;

    public TestRunner(ResultStore store, int? timeoutSeconds = null, string? filter = null)
    {
        _store = store;
        _timeoutSeconds = timeoutSeconds ?? DefaultTimeoutSeconds;
        _filter = filter;
    }

    public async Task<int> RunTestsAsync(string[] args)
    {
        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var resultsDir = Path.Combine(_store.StoreFolder, runId);
        Directory.CreateDirectory(resultsDir);

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

        Console.WriteLine($"Running: {executable} {string.Join(" ", commandArgs)}");
        Console.WriteLine();

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) Console.WriteLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) Console.Error.WriteLine(e.Data);
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
}
