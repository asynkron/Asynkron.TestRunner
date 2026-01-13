using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Asynkron.TestRunner.Profiling;
using Asynkron.TestRunner.Protocol;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Asynkron.TestRunner;

/// <summary>
/// Manages communication with a worker process
/// </summary>
public class WorkerProcess : IAsyncDisposable
{
    private static readonly List<WorkerProcess> ActiveWorkers = new();
    private static readonly object WorkersLock = new();

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private bool _disposed;

    public WorkerProcess(Process process, string? traceFile = null)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        TraceFile = traceFile;

        lock (WorkersLock)
        {
            ActiveWorkers.Add(this);
        }
    }

    public string? TraceFile { get; }

    /// <summary>
    /// Kills all active worker processes (for Ctrl+C cleanup)
    /// </summary>
    public static void KillAll()
    {
        List<WorkerProcess> workers;
        lock (WorkersLock)
        {
            workers = ActiveWorkers.ToList();
            ActiveWorkers.Clear();
        }

        foreach (var worker in workers)
        {
            worker.Kill();
        }
    }

    /// <summary>
    /// Spawns a new worker process
    /// </summary>
    public static WorkerProcess Spawn(string? workerPath = null, WorkerProfilingOptions? profiling = null)
    {
        // Find worker executable
        var workerExe = workerPath ?? FindWorkerPath();
        var (startInfo, traceFile) = BuildStartInfo(workerExe, profiling);

        var process = new Process { StartInfo = startInfo };
        process.Start();

        return new WorkerProcess(process, traceFile);
    }

    private static (ProcessStartInfo StartInfo, string? TraceFile) BuildStartInfo(
        string workerExe,
        WorkerProfilingOptions? profiling)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (profiling?.Enabled != true)
        {
            startInfo.FileName = "dotnet";
            startInfo.Arguments = QuoteArgument(workerExe);
            return (startInfo, null);
        }

        Directory.CreateDirectory(profiling.OutputDirectory);
        var traceFile = BuildTraceFilePath(profiling);
        var args = new List<string> { "collect", "--show-child-io" };

        if (profiling.Memory)
        {
            args.Add("--profile");
            args.Add("gc-verbose");
        }

        var providers = new List<string>();
        if (profiling.Cpu)
        {
            providers.Add("Microsoft-DotNETCore-SampleProfiler");
        }

        if (profiling.Exception)
        {
            providers.Add(BuildExceptionProvider());
        }

        if (profiling.Latency)
        {
            providers.Add(BuildContentionProvider());
        }

        if (providers.Count > 0)
        {
            args.Add("--providers");
            args.Add(string.Join(",", providers));
        }

        args.Add("--output");
        args.Add(traceFile);
        args.Add("--");
        args.Add("dotnet");
        args.Add(workerExe);

        startInfo.FileName = "dotnet-trace";
        startInfo.Arguments = string.Join(" ", args.Select(QuoteArgument));
        return (startInfo, traceFile);
    }

    private static string BuildTraceFilePath(WorkerProfilingOptions profiling)
    {
        var traceParts = new List<string>();
        if (profiling.Cpu)
        {
            traceParts.Add("cpu");
        }

        if (profiling.Memory)
        {
            traceParts.Add("memory");
        }

        if (profiling.Latency)
        {
            traceParts.Add("latency");
        }

        if (profiling.Exception)
        {
            traceParts.Add("exception");
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var traceLabel = traceParts.Count == 0 ? "trace" : string.Join("-", traceParts);
        var filename = $"{profiling.Label}_{traceLabel}_{timestamp}.nettrace";
        return Path.Combine(profiling.OutputDirectory, filename);
    }

    private static string BuildExceptionProvider()
    {
        var keywordsValue = ClrTraceEventParser.Keywords.Exception;
        var keywords = ((ulong)keywordsValue).ToString("x", CultureInfo.InvariantCulture);
        return $"Microsoft-Windows-DotNETRuntime:0x{keywords}:4";
    }

    private static string BuildContentionProvider()
    {
        var keywordsValue = ClrTraceEventParser.Keywords.Contention | ClrTraceEventParser.Keywords.Threading;
        var keywords = ((ulong)keywordsValue).ToString("x", CultureInfo.InvariantCulture);
        return $"Microsoft-Windows-DotNETRuntime:0x{keywords}:4";
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Contains(' ') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
    }

    /// <summary>
    /// Sends a command to the worker
    /// </summary>
    public void Send(ProtocolMessage message)
    {
        ProtocolIO.Write(_stdin, message);
    }

    /// <summary>
    /// Reads events from the worker
    /// </summary>
    public async IAsyncEnumerable<ProtocolMessage> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            // Check if process died unexpectedly
            if (_process.HasExited)
            {
                throw new WorkerCrashedException(_process.ExitCode);
            }

            var message = await ProtocolIO.ReadAsync(_stdout, ct);

            // Null means stream ended - check if process crashed
            if (message == null)
            {
                if (_process.HasExited && _process.ExitCode != 0)
                {
                    throw new WorkerCrashedException(_process.ExitCode);
                }
                break;
            }

            yield return message;

            // Stop after completion or error
            if (message is RunCompletedEvent or ErrorEvent)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Discovers tests in the assembly
    /// </summary>
    public async Task<List<DiscoveredTestInfo>> DiscoverAsync(string assemblyPath, CancellationToken ct = default)
    {
        Send(new DiscoverCommand(assemblyPath));

        await foreach (var msg in ReadEventsAsync(ct))
        {
            if (msg is DiscoveredEvent discovered)
            {
                return discovered.Tests;
            }

if (msg is ErrorEvent error)
{
    throw new InvalidOperationException($"Worker error: {error.Message}");
}

        }

        return [];
    }

    /// <summary>
    /// Runs tests and streams results
    /// </summary>
    public async IAsyncEnumerable<ProtocolMessage> RunAsync(
        string assemblyPath,
        List<string>? tests = null,
        int? timeoutSeconds = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Send(new RunCommand(assemblyPath, tests, timeoutSeconds));

        await foreach (var msg in ReadEventsAsync(ct))
        {
            yield return msg;

            // Stop after run completes
            if (msg is RunCompletedEvent)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Kills the worker process
    /// </summary>
    public void Kill()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore if already dead
            }
        }
    }

    private static string FindWorkerPath()
    {
        // Look for worker in same directory as this assembly
        var assemblyDir = Path.GetDirectoryName(typeof(WorkerProcess).Assembly.Location)!;
        var workerDll = Path.Combine(assemblyDir, "testrunner-worker.dll");

        if (File.Exists(workerDll))
        {
            return workerDll;
        }

        // Look in sibling Worker directory (dev scenario)
        var devPath = Path.Combine(assemblyDir, "..", "..", "..", "..",
            "Asynkron.TestRunner.Worker", "bin", "Debug", "net10.0", "testrunner-worker.dll");

        if (File.Exists(devPath))
        {
            return Path.GetFullPath(devPath);
        }

        throw new FileNotFoundException("Could not find testrunner-worker.dll");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (WorkersLock)
        {
            ActiveWorkers.Remove(this);
        }

        try
        {
            Send(new CancelCommand());
            await _stdin.DisposeAsync();
            _stdout.Dispose();

            if (!_process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Kill();
                }
            }

            _process.Dispose();
        }
        catch
        {
            // Cleanup errors are not critical
        }
    }
}
