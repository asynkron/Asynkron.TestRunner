using System.Diagnostics;
using Asynkron.TestRunner;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    // Handle --help
    if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h"))
    {
        PrintUsage();
        return 0;
    }

    // Handle "stats" subcommand
    if (args.Length > 0 && args[0].Equals("stats", StringComparison.OrdinalIgnoreCase))
    {
        var statsTestArgs = ExtractTestArgs(args, 1);
        var statsStore = new ResultStore(statsTestArgs);
        return HandleStats(args, statsStore, statsTestArgs);
    }

    // Handle "clear" subcommand
    if (args.Length > 0 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        var clearStore = new ResultStore();
        return HandleClear(clearStore);
    }

    // Handle "list" subcommand
    if (args.Length > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
    {
        var listFilter = args.Length > 1 && !args[1].StartsWith("-") && args[1] != "--" ? args[1] : null;
        var listTestArgs = ExtractTestArgs(args, 1) ?? ["dotnet", "test"];
        return await HandleListAsync(listTestArgs, listFilter);
    }

    // Handle "isolate" subcommand
    if (args.Length > 0 && args[0].Equals("isolate", StringComparison.OrdinalIgnoreCase))
    {
        var isolateFilter = ParseIsolateFilter(args);
        var isolateTimeout = ParseIsolateTimeout(args) ?? 30;
        var isolateParallel = ParseParallelism(args);
        var isolateTestArgs = ExtractTestArgs(args, 1) ?? ["dotnet", "test"];
        var isolateRunner = new IsolateRunner(isolateTestArgs, isolateTimeout, isolateFilter, isolateParallel);
        return await isolateRunner.RunAsync(isolateFilter);
    }

    // Handle "regressions" subcommand
    if (args.Length > 0 && args[0].Equals("regressions", StringComparison.OrdinalIgnoreCase))
    {
        var regTestArgs = ExtractTestArgs(args, 1);
        var regStore = new ResultStore(regTestArgs);
        return HandleRegressions(regStore);
    }

    // Parse options before the -- separator
    var timeout = ParseTimeout(args);
    var filter = ParseFilter(args);

    // Handle "--" separator for running tests
    var separatorIndex = Array.IndexOf(args, "--");
    string[] testArgs;

    if (separatorIndex >= 0)
    {
        testArgs = args.Skip(separatorIndex + 1).ToArray();
        if (testArgs.Length == 0)
        {
            // No command after --, default to "dotnet test"
            testArgs = ["dotnet", "test"];
        }
    }
    else if (args.Length == 0)
    {
        // No args at all, default to "dotnet test"
        testArgs = ["dotnet", "test"];
    }
    else if (args[0].StartsWith("dotnet", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("test", StringComparison.OrdinalIgnoreCase))
    {
        // First arg looks like a command, pass everything through
        testArgs = args[0].Equals("test", StringComparison.OrdinalIgnoreCase)
            ? args.Prepend("dotnet").ToArray()
            : args;
    }
    else if (filter != null)
    {
        // Has a filter pattern, default to "dotnet test"
        testArgs = ["dotnet", "test"];
    }
    else if (args.All(a => a.StartsWith("-") || a.StartsWith("--") || int.TryParse(a, out _)))
    {
        // Only options (like --timeout 30), default to "dotnet test"
        testArgs = ["dotnet", "test"];
    }
    else
    {
        // Unknown args, show help
        PrintUsage();
        return 1;
    }

    // Inject filter if specified
    if (filter != null)
    {
        testArgs = InjectFilter(testArgs, filter);
    }

    var store = new ResultStore(testArgs);
    var runner = new TestRunner(store, timeout, filter);
    return await runner.RunTestsAsync(testArgs);
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

static int? ParseTimeout(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase) ||
            args[i].Equals("-t", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds))
            {
                return seconds;
            }
        }
    }
    return null; // Use default
}

static int? ParseIsolateTimeout(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase) ||
            args[i].Equals("-t", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds))
            {
                return seconds;
            }
        }
    }
    return null;
}

static string? ParseIsolateFilter(string[] args)
{
    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];

        if (arg == "--")
            break;

        if (arg.StartsWith("-"))
        {
            if ((arg.Equals("--timeout", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("-t", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("--parallel", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("--max-parallel", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("-p", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Length)
            {
                i++;
            }
            continue;
        }

        return arg;
    }

    return null;
}

static int? ParseParallelism(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (arg == "--")
            break;

        if (arg.Equals("--parallel", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--max-parallel", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var count))
            {
                return count;
            }
        }
    }
    return null;
}

static string? ParseFilter(string[] args)
{
    // Look for a filter pattern (non-flag argument before --)
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        // Stop at separator
        if (arg == "--")
            break;

        // Skip known subcommands
        if (arg.Equals("stats", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("regressions", StringComparison.OrdinalIgnoreCase))
            continue;

        // Skip flags and their values
        if (arg.StartsWith("-"))
        {
            // Skip value of --timeout/-t
            if ((arg.Equals("--timeout", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("-t", StringComparison.OrdinalIgnoreCase)) &&
                i + 1 < args.Length)
            {
                i++;
            }
            continue;
        }

        // Skip command-like args
        if (arg.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("test", StringComparison.OrdinalIgnoreCase))
            continue;

        // This looks like a filter pattern
        return arg;
    }
    return null;
}

static string[] InjectFilter(string[] args, string filter)
{
    var list = args.ToList();

    // Check if --filter is already specified
    var hasFilter = list.Any(a =>
        a.StartsWith("--filter", StringComparison.OrdinalIgnoreCase));

    if (!hasFilter)
    {
        list.Add("--filter");
        list.Add($"FullyQualifiedName~{filter}");
    }

    return list.ToArray();
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

static async Task<int> HandleListAsync(string[] testArgs, string? filter)
{
    var argsList = testArgs.ToList();

    // Add --list-tests flag
    argsList.Add("--list-tests");

    // Inject filter if specified
    if (filter != null)
    {
        argsList.Add("--filter");
        argsList.Add($"FullyQualifiedName~{filter}");
    }

    var executable = "dotnet";
    var commandArgs = argsList.ToArray();

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

    Console.WriteLine($"Listing: {executable} {string.Join(" ", commandArgs)}");
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
    return process.ExitCode;
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
          testrunner                                           Run 'dotnet test' with defaults
          testrunner "pattern"                                 Run tests matching pattern
          testrunner [options] -- dotnet test [test-options]   Run tests with custom command
          testrunner list ["pattern"]                          List tests without running them
          testrunner isolate ["pattern"]                       Find hanging test by namespace isolation
          testrunner stats [-- <command>]                      Show history (optionally for specific command)
          testrunner stats --history N                         Show last N runs (default: 10)
          testrunner regressions [-- <command>]                Show regressions vs previous run
          testrunner clear                                     Clear all test history

        Options:
          -t, --timeout <seconds>    Hang detection timeout in seconds (default: 20)
                                     If a test runs longer than this, the test host is killed
                                     Use --timeout 0 to disable hang detection
          -p, --parallel <count>     Max parallel isolate batches (default: 1)
                                     Can also be set with TESTRUNNER_ISOLATE_PARALLEL
          -h, --help                 Show this help message

        Filter Pattern:
          testrunner "MyClass"           Runs: dotnet test --filter "FullyQualifiedName~MyClass"
          testrunner "Namespace.Test"    Matches any test containing that pattern

        Isolate Command:
          Finds hanging tests by running namespace groups in isolation.
          Drills down hierarchically until the hanging test(s) are found.
          testrunner isolate                           Isolate in all tests
          testrunner isolate "LanguageTests"           Isolate within matching tests
          testrunner isolate --timeout 60 "Tests"      Use 60s timeout (default: 30s)
          testrunner isolate --parallel 4 "Tests"      Run up to 4 batches in parallel

        Examples:
          testrunner                                   Run all tests
          testrunner "UserService"                     Run tests matching 'UserService'
          testrunner list "UserService"                List tests matching 'UserService'
          testrunner isolate "SlowTests"               Find hanging test in SlowTests
          testrunner --timeout 60 "SlowTests"          Run matching tests with 60s timeout
          testrunner --timeout 0                       Run all tests with no timeout
          testrunner -- dotnet test ./tests/MyTests    Run specific project

        History is tracked separately per:
          - Project (git repo or current directory)
          - Command signature (test path + filters)

        This means running with --filter A won't mix with --filter B history.
        """);
}
