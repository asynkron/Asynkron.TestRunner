using System.Diagnostics;
using Asynkron.TestRunner.Models;

namespace Asynkron.TestRunner;

public class TestRunner
{
    private readonly ResultStore _store;

    public TestRunner(ResultStore store)
    {
        _store = store;
    }

    public async Task<int> RunTestsAsync(string[] args)
    {
        var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var resultsDir = Path.Combine(_store.StoreFolder, runId);
        Directory.CreateDirectory(resultsDir);

        // Build the command with TRX logger injected
        var processArgs = BuildArgsWithTrxLogger(args, resultsDir);

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

        return process.ExitCode;
    }

    private static string[] BuildArgsWithTrxLogger(string[] args, string resultsDir)
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

        return argsList.ToArray();
    }
}
