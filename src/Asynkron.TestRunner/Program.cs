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
        var statsStore = new ResultStore();
        return HandleStats(args, statsStore);
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
        return await HandleListAsync(args);
    }

    // Handle "tree" subcommand - hierarchical dump
    if (args.Length > 0 && args[0].Equals("tree", StringComparison.OrdinalIgnoreCase))
    {
        return await HandleTreeAsync(args);
    }

    // Handle "isolate" subcommand
    if (args.Length > 0 && args[0].Equals("isolate", StringComparison.OrdinalIgnoreCase))
    {
        return await HandleIsolateAsync(args);
    }

    // Handle "regressions" subcommand
    if (args.Length > 0 && args[0].Equals("regressions", StringComparison.OrdinalIgnoreCase))
    {
        var regStore = new ResultStore();
        return HandleRegressions(regStore);
    }

    // Handle "run" subcommand (or default if assembly paths provided)
    if (args.Length > 0 && (args[0].Equals("run", StringComparison.OrdinalIgnoreCase) || args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
    {
        return await HandleRunAsync(args);
    }

    // No valid command
    PrintUsage();
    return 1;
}

static async Task<int> HandleRunAsync(string[] args)
{
    var assemblyPaths = ExtractAssemblyPaths(args);
    var filter = ParseFilter(args);
    var timeout = ParseTimeout(args);
    var quiet = ParseQuiet(args);
    var workers = ParseWorkers(args) ?? 1;

    if (assemblyPaths.Count == 0)
    {
        Console.WriteLine("Error: No test assembly paths provided.");
        Console.WriteLine("Usage: testrunner run <assembly.dll> [assembly2.dll...] [--filter pattern]");
        return 1;
    }

    var store = new ResultStore();
    var runner = new TestRunner(store, timeout, filter, quiet, workers);
    return await runner.RunTestsAsync(assemblyPaths.ToArray());
}

static int? ParseWorkers(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--workers", StringComparison.OrdinalIgnoreCase) ||
            args[i].Equals("-w", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var workers))
            {
                return Math.Max(1, workers);
            }
            return Environment.ProcessorCount;
        }
    }
    return null;
}

static async Task<int> HandleListAsync(string[] args)
{
    var assemblyPaths = ExtractAssemblyPaths(args);
    var filter = ParseFilter(args);

    if (assemblyPaths.Count == 0)
    {
        Console.WriteLine("Error: No test assembly paths provided.");
        Console.WriteLine("Usage: testrunner list <assembly.dll> [--filter pattern]");
        return 1;
    }

    var testFilter = TestFilter.Parse(filter);
    var tests = await TestDiscovery.DiscoverTestsAsync(assemblyPaths, testFilter);

    Console.WriteLine($"Found {tests.Count} tests:");
    foreach (var test in tests.OrderBy(t => t.FullyQualifiedName))
    {
        Console.WriteLine($"  {test.FullyQualifiedName}");
    }

    return 0;
}

static async Task<int> HandleTreeAsync(string[] args)
{
    var assemblyPaths = ExtractAssemblyPaths(args);
    var filter = ParseFilter(args);
    var outputFile = ParseOutput(args);

    if (assemblyPaths.Count == 0)
    {
        Console.WriteLine("Error: No test assembly paths provided.");
        Console.WriteLine("Usage: testrunner tree <assembly.dll> [--output file.md] [--filter pattern]");
        return 1;
    }

    var testFilter = TestFilter.Parse(filter);
    var tests = await TestDiscovery.DiscoverTestsAsync(assemblyPaths, testFilter);

    Console.WriteLine($"Found {tests.Count} tests, building tree...");

    // Build hierarchical structure: Namespace -> Class -> Method -> Variants
    var tree = new SortedDictionary<string, SortedDictionary<string, SortedDictionary<string, List<string>>>>();

    foreach (var test in tests)
    {
        if (!tree.TryGetValue(test.Namespace, out var classes))
        {
            classes = new SortedDictionary<string, SortedDictionary<string, List<string>>>();
            tree[test.Namespace] = classes;
        }

        if (!classes.TryGetValue(test.ClassName, out var methods))
        {
            methods = new SortedDictionary<string, List<string>>();
            classes[test.ClassName] = methods;
        }

        if (!methods.TryGetValue(test.MethodName, out var variants))
        {
            variants = new List<string>();
            methods[test.MethodName] = variants;
        }

        // Add variant (display name or test case args)
        var variant = test.TestCaseArgs ?? test.DisplayName;
        if (variant != test.MethodName)
        {
            variants.Add(variant);
        }
    }

    // Generate output
    using var writer = outputFile != null
        ? new StreamWriter(outputFile)
        : new StreamWriter(Console.OpenStandardOutput());

    writer.WriteLine($"# Test Hierarchy ({tests.Count} tests)");
    writer.WriteLine();

    foreach (var (ns, classes) in tree)
    {
        writer.WriteLine($"## {ns}");
        writer.WriteLine();

        foreach (var (className, methods) in classes)
        {
            writer.WriteLine($"### {className}");
            writer.WriteLine();

            foreach (var (methodName, variants) in methods)
            {
                if (variants.Count == 0)
                {
                    writer.WriteLine($"- {methodName}");
                }
                else if (variants.Count == 1)
                {
                    writer.WriteLine($"- {methodName}");
                    writer.WriteLine($"  - {variants[0]}");
                }
                else
                {
                    writer.WriteLine($"- {methodName} ({variants.Count} variants)");
                    foreach (var v in variants.Take(10))
                    {
                        writer.WriteLine($"  - {v}");
                    }
                    if (variants.Count > 10)
                    {
                        writer.WriteLine($"  - ... and {variants.Count - 10} more");
                    }
                }
            }

            writer.WriteLine();
        }
    }

    if (outputFile != null)
    {
        Console.WriteLine($"Tree written to: {outputFile}");
    }

    return 0;
}

static string? ParseOutput(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--output", StringComparison.OrdinalIgnoreCase) ||
            args[i].Equals("-o", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
    }
    return null;
}

static async Task<int> HandleIsolateAsync(string[] args)
{
    var assemblyPaths = ExtractAssemblyPaths(args);
    var filter = ParseFilter(args);
    var timeout = ParseTimeout(args) ?? 30;
    var parallel = ParseParallel(args) ?? 1;

    if (assemblyPaths.Count == 0)
    {
        Console.WriteLine("Error: No test assembly paths provided.");
        Console.WriteLine("Usage: testrunner isolate <assembly.dll> [--filter pattern] [--timeout N] [--parallel N]");
        return 1;
    }

    var isolateRunner = new IsolateRunner(assemblyPaths.ToArray(), timeout, filter, parallel);
    return await isolateRunner.RunAsync(filter);
}

static List<string> ExtractAssemblyPaths(string[] args)
{
    return args
        .Where(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("-"))
        .Select(ExpandPath)
        .Where(File.Exists)
        .ToList();
}

static string ExpandPath(string path)
{
    if (path.StartsWith("~/") || path == "~")
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path == "~" ? home : Path.Combine(home, path[2..]);
    }
    return path;
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
    return null;
}

static int? ParseParallel(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--parallel", StringComparison.OrdinalIgnoreCase) ||
            args[i].Equals("-p", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parallel))
            {
                return Math.Max(1, parallel);
            }
            return Environment.ProcessorCount;
        }
    }
    return null;
}

static bool ParseQuiet(string[] args)
{
    return args.Any(a =>
        a.Equals("--quiet", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("-q", StringComparison.OrdinalIgnoreCase));
}

static string? ParseFilter(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--filter", StringComparison.OrdinalIgnoreCase) ||
            args[i].Equals("-f", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
    }
    return null;
}

static int HandleStats(string[] args, ResultStore store)
{
    var historyCount = 10;

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

    var current = runs[0];
    var previous = runs[1];

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
        testrunner - Native .NET Test Runner

        Usage:
          testrunner run <assembly.dll> [options]        Run tests in assembly
          testrunner list <assembly.dll> [options]       List tests without running
          testrunner isolate <assembly.dll> [options]    Find hanging tests
          testrunner stats                               Show test history
          testrunner regressions                         Compare last 2 runs
          testrunner clear                               Clear test history

        Options:
          -f, --filter <pattern>       Filter tests by pattern (Class=Foo, Method=Bar)
          -t, --timeout <seconds>      Per-test timeout (default: 20s for run, 30s for isolate)
          -w, --workers [N]            Run N worker processes in parallel (default: 1)
          -p, --parallel [N]           Run N batches in parallel (default: 1, or CPU count)
          -q, --quiet                  Suppress verbose output
          -h, --help                   Show this help

        Examples:
          testrunner run ./bin/Release/net8.0/MyTests.dll
          testrunner run MyTests.dll --filter "Class=UserTests"
          testrunner list MyTests.dll --filter "Method=Should"
          testrunner isolate MyTests.dll --timeout 60 --parallel 4

        Native Runner:
          This runner executes xUnit and NUnit tests directly without vstest.
          Tests are run in isolated worker processes for stability.
        """);
}
