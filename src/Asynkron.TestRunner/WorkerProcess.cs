using System.Diagnostics;
using System.Runtime.CompilerServices;
using Asynkron.TestRunner.Protocol;

namespace Asynkron.TestRunner;

/// <summary>
/// Manages communication with a worker process
/// </summary>
public class WorkerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private bool _disposed;

    private WorkerProcess(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
    }

    /// <summary>
    /// Spawns a new worker process
    /// </summary>
    public static WorkerProcess Spawn(string? workerPath = null)
    {
        // Find worker executable
        var workerExe = workerPath ?? FindWorkerPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = workerExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = startInfo };
        process.Start();

        return new WorkerProcess(process);
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
                break;
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
                return discovered.Tests;

            if (msg is ErrorEvent error)
                throw new Exception($"Worker error: {error.Message}");
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
            return workerDll;

        // Look in sibling Worker directory (dev scenario)
        var devPath = Path.Combine(assemblyDir, "..", "..", "..", "..",
            "Asynkron.TestRunner.Worker", "bin", "Debug", "net10.0", "testrunner-worker.dll");

        if (File.Exists(devPath))
            return Path.GetFullPath(devPath);

        throw new FileNotFoundException("Could not find testrunner-worker.dll");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

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
