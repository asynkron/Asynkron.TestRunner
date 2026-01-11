using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Asynkron.TestRunner.Protocol;

namespace Asynkron.TestRunner;

/// <summary>
/// HTTP server that exposes test runner functionality with live UI
/// </summary>
public class HttpServer
{
    private readonly int _port;
    private readonly HttpListener _listener;
    private readonly ResultStore _store;
    private CancellationTokenSource? _runCts;
    private Task? _currentRun;
    private TestRunStatus? _currentStatus;
    private readonly object _statusLock = new();
    private readonly ConcurrentDictionary<string, TestResultDetail> _testResults = new();

    public HttpServer(int port)
    {
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _store = new ResultStore();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        Console.WriteLine($"Test runner server listening on http://localhost:{_port}");
        Console.WriteLine("Endpoints:");
        Console.WriteLine("  POST /discover  - Discover tests in assembly");
        Console.WriteLine("  POST /run       - Run tests");
        Console.WriteLine("  GET  /status    - Get current run status");
        Console.WriteLine("  POST /cancel    - Cancel current run");
        Console.WriteLine("  GET  /result    - Get test result by name pattern");
        Console.WriteLine("  GET  /list      - List tests by status (all, passed, failed, crashed, hanging, skipped)");
        Console.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = HandleRequestAsync(context);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Server error: {ex.Message}");
            }
        }

        _listener.Stop();
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            var path = request.Url?.AbsolutePath ?? "/";
            var method = request.HttpMethod;

            var result = (path, method) switch
            {
                ("/discover", "POST") => await HandleDiscoverAsync(request),
                ("/run", "POST") => await HandleRunAsync(request),
                ("/status", "GET") => HandleStatus(),
                ("/cancel", "POST") => HandleCancel(),
                ("/result", "GET") => HandleResult(request),
                ("/list", "GET") => HandleList(request),
                _ => new { error = "Not found" }
            };

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var buffer = System.Text.Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
        }
        catch (Exception ex)
        {
            var error = JsonSerializer.Serialize(new { error = ex.Message });
            var buffer = System.Text.Encoding.UTF8.GetBytes(error);
            response.StatusCode = 500;
            response.ContentType = "application/json";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
        }
        finally
        {
            response.Close();
        }
    }

    private static async Task<object> HandleDiscoverAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        var req = JsonSerializer.Deserialize<DiscoverRequest>(body);

        if (string.IsNullOrEmpty(req?.Assembly))
        {
            return new { error = "Assembly path required" };
        }

        var filter = TestFilter.Parse(req.Filter);
        var tests = await TestDiscovery.DiscoverTestsAsync([req.Assembly], filter);

        return new
        {
            assembly = req.Assembly,
            count = tests.Count,
            tests = tests.Select(t => new { t.FullyQualifiedName, t.DisplayName }).ToList()
        };
    }

    private async Task<object> HandleRunAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        var req = JsonSerializer.Deserialize<RunRequest>(body);

        if (string.IsNullOrEmpty(req?.Assembly))
        {
            return new { error = "Assembly path required" };
        }

        // Cancel any existing run
        _runCts?.Cancel();
        if (_currentRun != null)
        {
            try { await _currentRun; } catch { }
        }

        _runCts = new CancellationTokenSource();
        var runId = Guid.NewGuid().ToString("N")[..8];

        // Clear previous test results
        _testResults.Clear();

        lock (_statusLock)
        {
            _currentStatus = new TestRunStatus
            {
                RunId = runId,
                State = "running",
                Assembly = req.Assembly,
                StartTime = DateTime.UtcNow
            };
        }

        // Start run in background
        _currentRun = Task.Run(async () =>
        {
            try
            {
                var runner = new TestRunner(
                    _store,
                    req.Timeout ?? 30,
                    hangTimeoutSeconds: null, // Use default (same as timeout)
                    req.Filter,
                    quiet: false, // Show UI!
                    req.Workers ?? 1,
                    resultCallback: result => _testResults[result.FullyQualifiedName] = result
                );

                var exitCode = await runner.RunTestsAsync([req.Assembly], _runCts.Token);

                lock (_statusLock)
                {
                    if (_currentStatus?.RunId == runId)
                    {
                        _currentStatus.State = exitCode == 0 ? "passed" : "failed";
                        _currentStatus.EndTime = DateTime.UtcNow;
                        _currentStatus.ExitCode = exitCode;

                        // Get results from store
                        var results = _store.GetRecentRuns(1).FirstOrDefault();
                        if (results != null)
                        {
                            _currentStatus.Passed = results.Passed;
                            _currentStatus.Failed = results.Failed;
                            _currentStatus.Skipped = results.Skipped;
                            _currentStatus.FailedTests = results.FailedTests;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_statusLock)
                {
                    if (_currentStatus?.RunId == runId)
                    {
                        _currentStatus.State = "error";
                        _currentStatus.Error = ex.Message;
                        _currentStatus.EndTime = DateTime.UtcNow;
                    }
                }
            }
        });

        return new { runId, state = "started" };
    }

    private object HandleStatus()
    {
        lock (_statusLock)
        {
            if (_currentStatus == null)
            {
                return new { state = "idle" };
            }

            // Compute live counts from _testResults
            var results = _testResults.Values.ToList();
            _currentStatus.Passed = results.Count(r => r.Status == "passed");
            _currentStatus.Failed = results.Count(r => r.Status == "failed");
            _currentStatus.Skipped = results.Count(r => r.Status == "skipped");
            _currentStatus.Crashed = results.Count(r => r.Status == "crashed");
            _currentStatus.Hanging = results.Count(r => r.Status == "hanging");
            _currentStatus.FailedTests = results.Where(r => r.Status == "failed").Select(r => r.FullyQualifiedName).ToList();
            _currentStatus.CrashedTests = results.Where(r => r.Status == "crashed").Select(r => r.FullyQualifiedName).ToList();
            _currentStatus.HangingTests = results.Where(r => r.Status == "hanging").Select(r => r.FullyQualifiedName).ToList();

            return _currentStatus;
        }
    }

    private object HandleCancel()
    {
        _runCts?.Cancel();
        return new { cancelled = true };
    }

    private object HandleResult(HttpListenerRequest request)
    {
        var pattern = request.QueryString["pattern"];
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new { error = "Pattern query parameter required (e.g., ?pattern=MyTest)" };
        }

        // Find matching tests (case-insensitive contains)
        var matches = _testResults
            .Where(kv => kv.Key.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .ToList();

        if (matches.Count == 0)
        {
            return new { error = $"No tests found matching '{pattern}'", totalTests = _testResults.Count };
        }

        return new
        {
            pattern,
            count = matches.Count,
            results = matches
        };
    }

    private object HandleList(HttpListenerRequest request)
    {
        var status = request.QueryString["status"]?.ToLowerInvariant() ?? "all";
        var pattern = request.QueryString["pattern"];

        var results = _testResults.Values.AsEnumerable();

        // Filter by status
        if (status != "all")
        {
            results = results.Where(r => r.Status.Equals(status, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by name pattern (optional)
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            results = results.Where(r => r.FullyQualifiedName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
        }

        var list = results.ToList();

        return new
        {
            status,
            pattern,
            count = list.Count,
            totalTests = _testResults.Count,
            tests = list.Select(t => new
            {
                name = t.FullyQualifiedName,
                displayName = t.DisplayName,
                status = t.Status,
                durationMs = t.DurationMs,
                hasError = !string.IsNullOrEmpty(t.ErrorMessage),
                hasOutput = !string.IsNullOrEmpty(t.Output)
            }).ToList()
        };
    }

    private record DiscoverRequest(string? Assembly, string? Filter);
    private record RunRequest(string? Assembly, string? Filter, int? Timeout, int? Workers);

    private class TestRunStatus
    {
        public string RunId { get; set; } = "";
        public string State { get; set; } = "";
        public string Assembly { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public int? ExitCode { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int Crashed { get; set; }
        public int Hanging { get; set; }
        public List<string>? FailedTests { get; set; }
        public List<string>? CrashedTests { get; set; }
        public List<string>? HangingTests { get; set; }
        public string? Error { get; set; }
    }
}
