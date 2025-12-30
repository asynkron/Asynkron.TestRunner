using Asynkron.TestRunner;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    // Parse commands
    if (args.Length == 0)
    {
        PrintUsage();
        return 0;
    }

    // Handle "stats" subcommand
    if (args[0].Equals("stats", StringComparison.OrdinalIgnoreCase))
    {
        var testArgs = ExtractTestArgs(args, 1);
        var store = new ResultStore(testArgs);
        return HandleStats(args, store, testArgs);
    }

    // Handle "clear" subcommand
    if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        var store = new ResultStore();
        return HandleClear(store);
    }

    // Handle "regressions" subcommand
    if (args[0].Equals("regressions", StringComparison.OrdinalIgnoreCase))
    {
        var testArgs = ExtractTestArgs(args, 1);
        var store = new ResultStore(testArgs);
        return HandleRegressions(store);
    }

    // Handle "--" separator for running tests
    var separatorIndex = Array.IndexOf(args, "--");
    if (separatorIndex >= 0)
    {
        var testArgs = args.Skip(separatorIndex + 1).ToArray();
        if (testArgs.Length == 0)
        {
            Console.Error.WriteLine("Error: No command specified after --");
            return 1;
        }

        var store = new ResultStore(testArgs);
        var runner = new TestRunner(store);
        return await runner.RunTestsAsync(testArgs);
    }

    // If first arg looks like a command, pass everything through
    if (args[0].StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
        args[0].Equals("test", StringComparison.OrdinalIgnoreCase))
    {
        var testArgs = args[0].Equals("test", StringComparison.OrdinalIgnoreCase)
            ? args.Prepend("dotnet").ToArray()
            : args;

        var store = new ResultStore(testArgs);
        var runner = new TestRunner(store);
        return await runner.RunTestsAsync(testArgs);
    }

    PrintUsage();
    return 1;
}

static string[]? ExtractTestArgs(string[] args, int startIndex)
{
    var separatorIndex = Array.IndexOf(args, "--", startIndex);
    if (separatorIndex >= 0 && separatorIndex + 1 < args.Length)
    {
        return args.Skip(separatorIndex + 1).ToArray();
    }
    return null;
}

static int HandleStats(string[] args, ResultStore store, string[]? testArgs)
{
    var historyCount = 10; // default

    // Parse --history N
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].Equals("--history", StringComparison.OrdinalIgnoreCase) ||
            args[i].Equals("-n", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var count))
            {
                historyCount = count;
            }
        }
    }

    if (testArgs != null)
    {
        Console.WriteLine($"History for: {store.CommandSignature}");
    }

    var results = store.GetRecentRuns(historyCount);
    ChartRenderer.RenderHistory(results);
    return 0;
}

static int HandleClear(ResultStore store)
{
    if (Directory.Exists(store.BaseFolder))
    {
        Directory.Delete(store.BaseFolder, recursive: true);
        Console.WriteLine("All test history cleared for this project.");
    }
    else
    {
        Console.WriteLine("No test history found.");
    }
    return 0;
}

static int HandleRegressions(ResultStore store)
{
    var runs = store.GetRecentRuns(2);

    if (runs.Count < 2)
    {
        Console.WriteLine("Need at least 2 runs to compare regressions.");
        return 1;
    }

    var current = runs[0];  // Most recent
    var previous = runs[1]; // Previous

    Console.WriteLine();
    Console.WriteLine($"Comparing: {current.Timestamp:yyyy-MM-dd HH:mm} vs {previous.Timestamp:yyyy-MM-dd HH:mm}");

    ChartRenderer.RenderRegressions(current, previous);

    if (current.GetRegressions(previous).Count == 0 && current.GetFixes(previous).Count == 0)
    {
        Console.WriteLine("No regressions or fixes detected.");
    }

    Console.WriteLine();
    return 0;
}

static void PrintUsage()
{
    Console.WriteLine("""
        testrunner - .NET Test Runner with History

        Usage:
          testrunner -- dotnet test [options]           Run tests and capture results
          testrunner stats -- dotnet test [options]     Show history for specific command
          testrunner stats --history N -- <command>     Show last N runs (default: 10)
          testrunner regressions -- <command>           Show regressions vs previous run
          testrunner clear                              Clear all test history

        Examples:
          testrunner -- dotnet test ./tests/MyTests
          testrunner -- dotnet test --filter "Category=Unit"
          testrunner stats -- dotnet test ./tests/MyTests
          testrunner regressions -- dotnet test --filter "Category=Unit"

        History is tracked separately per:
          - Project (git repo or current directory)
          - Command signature (test path + filters)

        This means running with --filter A won't mix with --filter B history.
        """);
}
