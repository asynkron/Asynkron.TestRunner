using System.Runtime.InteropServices;
using Asynkron.TestRunner;
using Asynkron.TestRunner.Profiling;

var signalRegistrations = RegisterShutdownHandlers();

return await RunAsync(args);

static List<PosixSignalRegistration> RegisterShutdownHandlers()
{
    // Handle Ctrl+C to kill worker processes
    Console.CancelKeyPress += (_, _) =>
    {
        WorkerProcess.KillAll();
        Environment.Exit(130); // Standard exit code for Ctrl+C
    };

    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        WorkerProcess.KillAll();
    };

    var registrations = new List<PosixSignalRegistration>();
    if (!OperatingSystem.IsWindows())
    {
        registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => WorkerProcess.KillAll()));
        registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGQUIT, _ => WorkerProcess.KillAll()));
    }

    return registrations;
}

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

    // Handle "serve" subcommand - HTTP server with UI
    if (args.Length > 0 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
    {
        return await HandleServeAsync(args);
    }

    // Handle "mcp" subcommand - MCP server (proxies to HTTP server)
    if (args.Length > 0 && args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase))
    {
        return await HandleMcpAsync(args);
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

    // No arguments or only options (no subcommand, no paths) - auto-detect and run test projects
    if (args.Length == 0 || IsOnlyOptions(args))
    {
        return await HandleAutoRunAsync(args);
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
    var hangTimeout = ParseHangTimeout(args);
    var quiet = ParseQuiet(args);
    var streamingConsole = ParseConsole(args);
    var treeView = ParseTreeView(args);
    var treeDepth = ParseTreeDepth(args);
    var workers = ParseWorkers(args) ?? 4;
    var verbose = ParseVerbose(args);
    var logFile = ParseLogFile(args);
    var resumeEnabled = ParseResume(args, out var resumeFile);
    var profilingSettings = ParseProfilingSettings(args);
    var ghBugReport = ParseGhBugReport(args);
    var historySelection = ParseHistorySelection(args);

    if (assemblyPaths.Count == 0)
    {
        Console.WriteLine("Error: No test assemblies or projects provided.");
        Console.WriteLine("Usage: testrunner run <assembly.dll|project.csproj> [--filter pattern]");
        return 1;
    }

    var (store, selectedTests, historyLabel, selectionError, shouldExit, exitCode) =
        ResolveHistorySelection(historySelection);

    if (shouldExit)
    {
        if (!string.IsNullOrWhiteSpace(selectionError))
        {
            Console.WriteLine(selectionError);
        }
        else if (!string.IsNullOrWhiteSpace(historyLabel))
        {
            Console.WriteLine(historyLabel);
        }
        return exitCode;
    }

    var treeSettings = treeView ? new TreeViewSettings { MaxDepth = treeDepth } : null;
    if (!string.IsNullOrWhiteSpace(historyLabel))
    {
        Console.WriteLine(historyLabel);
    }

    var runner = new TestRunner(store, timeout, hangTimeout, filter, quiet, streamingConsole, treeView, treeSettings, workers, verbose, logFile, resumeEnabled ? resumeFile : null, profilingSettings, includedTests: selectedTests, historyFilterLabel: historyLabel, ghBugReport: ghBugReport);
    return await runner.RunTestsAsync(assemblyPaths.ToArray());
}

static async Task<int> HandleAutoRunAsync(string[] args)
{
    var testProjects = FindTestProjects(Environment.CurrentDirectory);
    
    if (testProjects.Count == 0)
    {
        Console.WriteLine("No test projects found in current directory.");
        Console.WriteLine();
        Console.WriteLine("Looking for projects matching patterns:");
        Console.WriteLine("  - *.Tests.csproj");
        Console.WriteLine("  - *.Test.csproj");
        Console.WriteLine("  - *Tests.csproj");
        Console.WriteLine("  - *Test.csproj");
        Console.WriteLine();
        Console.WriteLine("Or run with explicit path: testrunner run <path.csproj>");
        return 1;
    }

    Console.WriteLine($"Found {testProjects.Count} test project(s):");
    foreach (var project in testProjects)
    {
        Console.WriteLine($"  - {Path.GetRelativePath(Environment.CurrentDirectory, project)}");
    }
    Console.WriteLine();

    var overallExitCode = 0;
    var results = new List<(string Project, int ExitCode, TimeSpan Duration)>();

    var historySelection = ParseHistorySelection(args);
    var (store, selectedTests, historyLabel, selectionError, shouldExit, selectionExitCode) =
        ResolveHistorySelection(historySelection);

    if (shouldExit)
    {
        if (!string.IsNullOrWhiteSpace(selectionError))
        {
            Console.WriteLine(selectionError);
        }
        else if (!string.IsNullOrWhiteSpace(historyLabel))
        {
            Console.WriteLine(historyLabel);
        }
        return selectionExitCode;
    }

    foreach (var project in testProjects)
    {
        var projectName = Path.GetFileNameWithoutExtension(project);
        Console.WriteLine($"{'=',-60}");
        Console.WriteLine($"Running: {projectName}");
        Console.WriteLine($"{'=',-60}");
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        // Build args array with project path
        var runArgs = new[] { "run", project }.Concat(args).ToArray();

        if (!string.IsNullOrWhiteSpace(historyLabel))
        {
            Console.WriteLine(historyLabel);
        }

        var filter = ParseFilter(runArgs);
        var timeout = ParseTimeout(runArgs);
        var hangTimeout = ParseHangTimeout(runArgs);
        var quiet = ParseQuiet(runArgs);
        var streamingConsole = ParseConsole(runArgs);
        var treeView = ParseTreeView(runArgs);
        var treeDepth = ParseTreeDepth(runArgs);
        var workers = ParseWorkers(runArgs) ?? 4;
        var verbose = ParseVerbose(runArgs);
        var logFile = ParseLogFile(runArgs);
        var resumeEnabled = ParseResume(runArgs, out var resumeFile);
        var profilingSettings = ParseProfilingSettings(runArgs);
        var ghBugReport = ParseGhBugReport(runArgs);
        var treeSettings = treeView ? new TreeViewSettings { MaxDepth = treeDepth } : null;

        var assemblyPaths = ExtractAssemblyPaths(runArgs);
        var runner = new TestRunner(store, timeout, hangTimeout, filter, quiet, streamingConsole, treeView, treeSettings, workers, verbose, logFile, resumeEnabled ? resumeFile : null, profilingSettings, includedTests: selectedTests, historyFilterLabel: historyLabel, ghBugReport: ghBugReport);
        var runExitCode = await runner.RunTestsAsync(assemblyPaths.ToArray());
        
        stopwatch.Stop();
        results.Add((projectName, runExitCode, stopwatch.Elapsed));
        
        if (runExitCode != 0)
        {
            overallExitCode = 1;
        }
        
        Console.WriteLine();
    }

    // Print summary
    Console.WriteLine($"{'=',-60}");
    Console.WriteLine("Summary");
    Console.WriteLine($"{'=',-60}");
    
    foreach (var (project, projectExitCode, duration) in results)
    {
        var status = projectExitCode == 0 ? "\u001b[32mPASSED\u001b[0m" : "\u001b[31mFAILED\u001b[0m";
        Console.WriteLine($"  {status} {project} ({duration.TotalSeconds:F1}s)");
    }
    
    Console.WriteLine();
    var passed = results.Count(r => r.ExitCode == 0);
    var failed = results.Count(r => r.ExitCode != 0);
    Console.WriteLine($"Total: {passed} passed, {failed} failed");
    
    return overallExitCode;
}

static HistorySelection ParseHistorySelection(string[] args)
{
    var selection = HistorySelection.None;
    if (HasOption(args, "--history-passing"))
    {
        selection |= HistorySelection.Passing;
    }

    if (HasOption(args, "--history-failing"))
    {
        selection |= HistorySelection.Failing;
    }

    if (HasOption(args, "--history-hanging"))
    {
        selection |= HistorySelection.Hanging;
    }

    if (HasOption(args, "--history-crashing"))
    {
        selection |= HistorySelection.Crashing;
    }

    return selection;
}

static (ResultStore Store, IReadOnlyCollection<string>? SelectedTests, string? HistoryLabel, string? Error, bool ShouldExit, int ExitCode)
    ResolveHistorySelection(HistorySelection selection)
{
    if (selection == HistorySelection.None)
    {
        return (new ResultStore(), null, null, null, false, 0);
    }

    var probeStore = new ResultStore();
    var historyFile = probeStore.FindLatestHistoryFile();

    if (string.IsNullOrWhiteSpace(historyFile))
    {
        return (probeStore, null, null, "No test history found for this repo. Run `testrunner` once to generate `.testrunner/**/history.json`.", true, 1);
    }

    var latestRun = ResultStore.GetLatestRun(historyFile);
    if (latestRun == null)
    {
        return (probeStore, null, null, $"History file is empty or invalid: {historyFile}", true, 1);
    }

    var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if (selection.HasFlag(HistorySelection.Passing))
    {
        selected.UnionWith(latestRun.PassedTests);
    }

    if (selection.HasFlag(HistorySelection.Failing))
    {
        selected.UnionWith(latestRun.FailedTests);
    }

    if (selection.HasFlag(HistorySelection.Hanging))
    {
        if (latestRun.HangingTests.Count > 0)
        {
            selected.UnionWith(latestRun.HangingTests);
        }
        else
        {
            selected.UnionWith(latestRun.TimedOutTests);
        }
    }

    if (selection.HasFlag(HistorySelection.Crashing))
    {
        if (latestRun.CrashedTests.Count > 0)
        {
            selected.UnionWith(latestRun.CrashedTests);
        }
        else
        {
            selected.UnionWith(latestRun.TimedOutTests);
        }
    }

    var store = ResultStore.FromHistoryFile(historyFile);
    var selectionText = DescribeHistorySelection(selection);
    var label = $"History: {selectionText} from {latestRun.Timestamp:yyyy-MM-dd HH:mm:ss} ({selected.Count} tests)";

    if (selected.Count == 0)
    {
        return (store, null, label + " (none)", null, true, 0);
    }

    return (store, selected, label, null, false, 0);
}

static string DescribeHistorySelection(HistorySelection selection)
{
    var parts = new List<string>();
    if (selection.HasFlag(HistorySelection.Passing))
    {
        parts.Add("passing");
    }

    if (selection.HasFlag(HistorySelection.Failing))
    {
        parts.Add("failing");
    }

    if (selection.HasFlag(HistorySelection.Hanging))
    {
        parts.Add("hanging");
    }

    if (selection.HasFlag(HistorySelection.Crashing))
    {
        parts.Add("crashing");
    }

    return parts.Count == 0 ? "none" : string.Join("+", parts);
}

static List<string> FindTestProjects(string rootDir)
{
    var testProjects = new List<string>();
    
    // Common test project naming patterns
    var patterns = new[]
    {
        "*.Tests.csproj",
        "*.Test.csproj",
        "*Tests.csproj",
        "*Test.csproj",
        "*.IntegrationTests.csproj",
        "*.UnitTests.csproj"
    };

    try
    {
        foreach (var pattern in patterns)
        {
            var matches = Directory.GetFiles(rootDir, pattern, SearchOption.AllDirectories);
            foreach (var match in matches)
            {
                // Skip bin/obj directories
                if (match.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    match.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }
                
                if (!testProjects.Contains(match))
                {
                    testProjects.Add(match);
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Warning: Error scanning for test projects: {ex.Message}");
    }

    // Sort by path for consistent ordering
    testProjects.Sort();
    
    return testProjects;
}

static bool IsOnlyOptions(string[] args)
{
    // Check if all arguments are options (--flag) or option values (following an option that takes a value)
    var optionsWithValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "-f", "--filter",
        "-t", "--timeout",
        "--hang-timeout",
        "-w", "--workers",
        "--log",
        "--resume",
        "--tree-depth",
        "--port",
        "--root"
    };

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        
        // If it starts with -, it's an option
        if (arg.StartsWith('-'))
        {
            // If this option takes a value, skip the next argument
            if (optionsWithValues.Contains(arg) && i + 1 < args.Length)
            {
                i++; // Skip the value
            }
            continue;
        }
        
        // This is a non-option argument that's not a value for a previous option
        // It could be a subcommand or a path
        return false;
    }
    
    return true;
}

static bool HasOption(string[] args, params string[] options)
{
    return args.Any(arg => options.Any(option => arg.Equals(option, StringComparison.OrdinalIgnoreCase)));
}

static string? GetOptionValue(string[] args, params string[] options)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (options.Any(option => args[i].Equals(option, StringComparison.OrdinalIgnoreCase)))
        {
            if (i + 1 < args.Length)
            {
                return args[i + 1];
            }

            return null;
        }
    }

    return null;
}

static int? GetOptionInt(string[] args, params string[] options)
{
    var value = GetOptionValue(args, options);
    if (value != null && int.TryParse(value, out var parsed))
    {
        return parsed;
    }

    return null;
}

static bool ParseVerbose(string[] args)
{
    return HasOption(args, "--verbose", "-v");
}

static string? ParseLogFile(string[] args)
{
    if (!HasOption(args, "--log"))
    {
        return null;
    }

    var value = GetOptionValue(args, "--log");
    return string.IsNullOrWhiteSpace(value) ? "testrunner.log" : value;
}

static bool ParseResume(string[] args, out string? resumeFile)
{
    resumeFile = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].Equals("--resume", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                resumeFile = args[i + 1];
            }
            else
            {
                resumeFile = string.Empty;
            }
            return true;
        }
    }
    return false;
}

static async Task<int> HandleServeAsync(string[] args)
{
    var port = ParsePort(args) ?? 5123;

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var server = new HttpServer(port);
    await server.RunAsync(cts.Token);
    return 0;
}

static async Task<int> HandleMcpAsync(string[] args)
{
    var port = ParsePort(args) ?? 5123;

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var mcp = new McpServer(port);
    await mcp.RunAsync(cts.Token);
    return 0;
}

static int? ParsePort(string[] args)
{
    return GetOptionInt(args, "--port");
}

static int? ParseWorkers(string[] args)
{
    var value = GetOptionValue(args, "--workers", "-w");
    if (value != null && int.TryParse(value, out var workers))
    {
        return Math.Max(1, workers);
    }

    return HasOption(args, "--workers", "-w") ? Environment.ProcessorCount : null;
}

static async Task<int> HandleListAsync(string[] args)
{
    var assemblyPaths = ExtractAssemblyPaths(args);
    var filter = ParseFilter(args);

    if (assemblyPaths.Count == 0)
    {
        Console.WriteLine("Error: No test assemblies or projects provided.");
        Console.WriteLine("Usage: testrunner list <assembly.dll|project.csproj> [--filter pattern]");
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
        Console.WriteLine("Error: No test assemblies or projects provided.");
        Console.WriteLine("Usage: testrunner tree <assembly.dll|project.csproj> [--output file.md] [--filter pattern]");
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
    return GetOptionValue(args, "--output", "-o");
}

static List<string> ExtractAssemblyPaths(string[] args)
{
    var paths = new List<string>();

    foreach (var arg in args)
    {
        if (arg.StartsWith("-"))
        {
            continue;
        }

        var expandedPath = ExpandPath(arg);

        // Handle .csproj files - build them first
        if (arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(expandedPath))
            {
                Console.WriteLine($"Warning: Project file not found: {expandedPath}");
                continue;
            }

            var dllPath = BuildProject(expandedPath);
            if (dllPath != null)
            {
                paths.Add(dllPath);
            }
            continue;
        }

        // Handle .dll files directly
        if (arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(expandedPath))
            {
                paths.Add(expandedPath);
            }
            else
            {
                Console.WriteLine($"Warning: Assembly not found: {expandedPath}");
            }
        }
    }

    return paths;
}

static string? BuildProject(string csprojPath)
{
    Console.WriteLine($"Building project: {Path.GetFileName(csprojPath)}");

    var startInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"build \"{csprojPath}\" -c Release --nologo",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    try
    {
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null)
        {
            Console.WriteLine("Error: Failed to start dotnet build process");
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            Console.WriteLine($"Error: Build failed with exit code {process.ExitCode}");
            if (!string.IsNullOrWhiteSpace(error))
            {
                Console.WriteLine(error);
            }
            return null;
        }

        // Find the output DLL
        var projectDir = Path.GetDirectoryName(csprojPath);
        var projectName = Path.GetFileNameWithoutExtension(csprojPath);

        if (projectDir == null)
        {
            Console.WriteLine("Error: Could not determine project directory");
            return null;
        }

        // Search for the DLL in bin/Release/net*/
        var binReleasePath = Path.Combine(projectDir, "bin", "Release");
        if (!Directory.Exists(binReleasePath))
        {
            Console.WriteLine($"Error: Release output directory not found: {binReleasePath}");
            return null;
        }

        // Find all target framework directories
        var tfmDirs = Directory.GetDirectories(binReleasePath, "net*");
        if (tfmDirs.Length == 0)
        {
            Console.WriteLine($"Error: No target framework directories found in {binReleasePath}");
            return null;
        }

        // Try to find the DLL in the first (or latest) target framework directory
        var tfmDir = tfmDirs.OrderByDescending(d => d).First();
        var dllPath = Path.Combine(tfmDir, $"{projectName}.dll");

        if (!File.Exists(dllPath))
        {
            Console.WriteLine($"Error: Output DLL not found: {dllPath}");
            return null;
        }

        Console.WriteLine($"Built successfully: {dllPath}");
        return dllPath;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error building project: {ex.Message}");
        return null;
    }
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
    return GetOptionInt(args, "--timeout", "-t");
}

static int? ParseHangTimeout(string[] args)
{
    return GetOptionInt(args, "--hang-timeout");
}

static bool ParseQuiet(string[] args)
{
    return HasOption(args, "--quiet", "-q");
}

static bool ParseConsole(string[] args)
{
    return HasOption(args, "--console", "-c");
}

static bool ParseTreeView(string[] args)
{
    return HasOption(args, "--tree-view", "--tree");
}

static int ParseTreeDepth(string[] args)
{
    return GetOptionInt(args, "--tree-depth") ?? 1; // Default: namespace + class
}

static string? ParseFilter(string[] args)
{
    return GetOptionValue(args, "--filter", "-f");
}

static bool ParseGhBugReport(string[] args)
{
    return HasOption(args, "--gh-bugreport", "--gh-issues");
}

static bool ParseProfileCpu(string[] args)
{
    return HasOption(args, "--profile-cpu");
}

static bool ParseProfileMemory(string[] args)
{
    return HasOption(args, "--profile-memory");
}

static bool ParseProfileLatency(string[] args)
{
    return HasOption(args, "--profile-latency");
}

static bool ParseProfileException(string[] args)
{
    return HasOption(args, "--profile-exception");
}

static string? ParseProfileRoot(string[] args)
{
    return GetOptionValue(args, "--root");
}

static WorkerProfilingSettings? ParseProfilingSettings(string[] args)
{
    var settings = new WorkerProfilingSettings(
        ParseProfileCpu(args),
        ParseProfileMemory(args),
        ParseProfileLatency(args),
        ParseProfileException(args),
        ParseProfileRoot(args));

    return settings.Enabled ? settings : null;
}

static int HandleStats(string[] args, ResultStore store)
{
    var historyCount = GetOptionInt(args, "--history", "-n") ?? 10;

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
          testrunner                                     Auto-detect and run all test projects
          testrunner run <path> [options]                Run tests (.dll or .csproj)
          testrunner list <path> [options]               List tests without running
          testrunner serve [--port N]                    Start HTTP server with UI (for MCP)
          testrunner mcp [--port N]                      Start MCP server (connects to serve)
          testrunner stats                               Show test history
          testrunner regressions                         Compare last 2 runs
          testrunner clear                               Clear test history

        Options:
          -f, --filter <pattern>       Filter tests by pattern (or simple substring)
          -t, --timeout <seconds>      Per-test timeout (default: 30s)
          -w, --workers [N]            Run N worker processes in parallel (default: 4; flag alone uses CPU count)
          -q, --quiet                  Suppress verbose output
          -c, --console                Streaming console mode (no interactive UI)
          --tree-view, --tree          Tree view UI (scrollable namespace/class hierarchy)
          --tree-depth <N>             Tree depth: 0=namespace, 1=class (default), 2=method
          -v, --verbose                Show diagnostic logs on stderr
          --log <file>                 Write diagnostic logs to file
          --resume [file]              Resume from checkpoint (default: .testrunner/resume.jsonl)
          --history-passing            Re-run tests that passed in the latest recorded run
          --history-failing            Re-run tests that failed in the latest recorded run
          --history-hanging            Re-run tests that hung/timed out in the latest recorded run
          --history-crashing           Re-run tests that crashed in the latest recorded run
          --gh-bugreport               Auto-create GitHub issues for failing tests (requires gh CLI)
          --profile-cpu                Collect CPU sampling traces per worker
          --profile-memory             Collect allocation traces per worker
          --profile-latency            Collect contention/latency traces per worker
          --profile-exception          Collect exception traces per worker
          --root <substring>           Call tree root filter for profiling output
          -h, --help                   Show this help

        Examples:
          testrunner                                     # Auto-detect and run all test projects
          testrunner --history-failing                    # Re-run failing tests from last run
          testrunner --history-passing                    # Re-run passing tests (regression check)
          testrunner run ./bin/Release/net8.0/MyTests.dll
          testrunner run MyTests.csproj --console --filter "UserTests"
          testrunner run MyTests.dll --filter "Class=UserTests"
          testrunner run MyTests.dll --gh-bugreport
          testrunner list MyTests.csproj --filter "Should"

        Features:
          - Auto-detects test projects (*.Tests.csproj, *.Test.csproj, etc.)
          - Supports .dll and .csproj files (.csproj builds in Release mode)
          - Executes xUnit and NUnit tests in isolated worker processes
          - Simple substring filtering or structured filters (Class=, Method=, etc.)
          - Auto-create GitHub issues for failures with --gh-bugreport (matches existing issues first)
        """);
}

[Flags]
enum HistorySelection
{
    None = 0,
    Passing = 1 << 0,
    Failing = 1 << 1,
    Hanging = 1 << 2,
    Crashing = 1 << 3
}
