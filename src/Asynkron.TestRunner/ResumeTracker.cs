using System.Text.Json;

namespace Asynkron.TestRunner;

internal sealed class ResumeTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly string _assemblyPath;
    private readonly string _runId;
    private readonly object _lock = new();
    private readonly List<string> _allTests;
    private readonly Dictionary<string, ResumeEntry> _completedIndex;

    private ResumeTracker(string filePath, string assemblyPath, IReadOnlyList<string> allTests)
    {
        _filePath = filePath;
        _assemblyPath = Path.GetFullPath(assemblyPath);

        var loaded = LoadState(filePath, _assemblyPath);
        _runId = loaded.RunId ?? Guid.NewGuid().ToString("N");
        _allTests = allTests.ToList();

        var testSet = new HashSet<string>(_allTests);
        _completedIndex = loaded.Completed
            .Where(entry => testSet.Contains(entry.Test))
            .GroupBy(entry => entry.Test)
            .Select(group => group.First())
            .ToDictionary(entry => entry.Test, entry => entry);

        if (loaded.RunId == null || !MatchesTests(loaded.StoredTests, _allTests))
        {
            AppendTestsLine();
        }
    }

    public static ResumeTracker? TryLoad(string? filePath, string assemblyPath, IReadOnlyList<string> allTests)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        return new ResumeTracker(filePath, assemblyPath, allTests);
    }

    public IReadOnlyList<string> AllTests
    {
        get
        {
            lock (_lock)
            {
                return _allTests.ToList();
            }
        }
    }

    public IReadOnlyList<ResumeEntry> CompletedEntries
    {
        get
        {
            lock (_lock)
            {
                return _completedIndex.Values.ToList();
            }
        }
    }

    public List<string> FilterPending()
    {
        lock (_lock)
        {
            return _allTests.Where(test => !_completedIndex.ContainsKey(test)).ToList();
        }
    }

    public void MarkCompleted(string testName, string status, string? displayName)
    {
        lock (_lock)
        {
            if (_completedIndex.ContainsKey(testName))
            {
                return;
            }

            var entry = new ResumeEntry
            {
                Test = testName,
                Status = status,
                DisplayName = displayName
            };

            _completedIndex[testName] = entry;
            AppendLine(new ResumeResultLine
            {
                AssemblyPath = _assemblyPath,
                RunId = _runId,
                Test = testName,
                Status = status,
                DisplayName = displayName,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    private static ResumeLoadResult LoadState(string filePath, string assemblyPath)
    {
        if (!File.Exists(filePath))
        {
            return new ResumeLoadResult(null, null, []);
        }

        string? latestRunId = null;
        List<string>? latestTests = null;
        var completedByRun = new Dictionary<string, Dictionary<string, ResumeEntry>>();

        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ResumeLine? resumeLine;
                try
                {
                    resumeLine = JsonSerializer.Deserialize<ResumeLine>(line, JsonOptions);
                }
                catch
                {
                    continue;
                }

                if (resumeLine?.Type == null)
                {
                    continue;
                }

                if (resumeLine.Type.Equals("tests", StringComparison.OrdinalIgnoreCase))
                {
                    ResumeTestsLine? testsLine;
                    try
                    {
                        testsLine = JsonSerializer.Deserialize<ResumeTestsLine>(line, JsonOptions);
                    }
                    catch
                    {
                        continue;
                    }

                    if (testsLine == null)
                    {
                        continue;
                    }

                    if (Path.GetFullPath(testsLine.AssemblyPath) != assemblyPath)
                    {
                        continue;
                    }

                    latestRunId = testsLine.RunId;
                    latestTests = testsLine.Tests ?? [];
                    continue;
                }

                if (resumeLine.Type.Equals("result", StringComparison.OrdinalIgnoreCase))
                {
                    ResumeResultLine? resultLine;
                    try
                    {
                        resultLine = JsonSerializer.Deserialize<ResumeResultLine>(line, JsonOptions);
                    }
                    catch
                    {
                        continue;
                    }

                    if (resultLine == null || string.IsNullOrEmpty(resultLine.RunId))
                    {
                        continue;
                    }

                    if (Path.GetFullPath(resultLine.AssemblyPath) != assemblyPath)
                    {
                        continue;
                    }

                    if (!completedByRun.TryGetValue(resultLine.RunId, out var entries))
                    {
                        entries = new Dictionary<string, ResumeEntry>();
                        completedByRun[resultLine.RunId] = entries;
                    }

                    if (!entries.ContainsKey(resultLine.Test))
                    {
                        entries[resultLine.Test] = new ResumeEntry
                        {
                            Test = resultLine.Test,
                            Status = resultLine.Status,
                            DisplayName = resultLine.DisplayName
                        };
                    }
                }
            }
        }
        catch
        {
            return new ResumeLoadResult(null, null, []);
        }

        if (latestRunId == null)
        {
            return new ResumeLoadResult(null, latestTests, []);
        }

        if (!completedByRun.TryGetValue(latestRunId, out var latestCompleted))
        {
            latestCompleted = new Dictionary<string, ResumeEntry>();
        }

        return new ResumeLoadResult(latestRunId, latestTests, latestCompleted.Values.ToList());
    }

    private void AppendTestsLine()
    {
        AppendLine(new ResumeTestsLine
        {
            AssemblyPath = _assemblyPath,
            RunId = _runId,
            Tests = _allTests.ToList(),
            Timestamp = DateTime.UtcNow
        });
    }

    private void AppendLine<T>(T payload)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            File.AppendAllText(_filePath, json + Environment.NewLine);
        }
        catch
        {
            // Ignore resume persistence failures
        }
    }

    private static bool MatchesTests(List<string>? storedTests, List<string> currentTests)
    {
        if (storedTests == null || storedTests.Count == 0)
        {
            return false;
        }

        if (storedTests.Count != currentTests.Count)
        {
            return false;
        }

        return new HashSet<string>(storedTests).SetEquals(currentTests);
    }

    private sealed record ResumeLoadResult(string? RunId, List<string>? StoredTests, List<ResumeEntry> Completed);

    private sealed class ResumeLine
    {
        public string? Type { get; set; }
    }

    private sealed class ResumeTestsLine
    {
        public string Type { get; set; } = "tests";
        public string AssemblyPath { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public List<string>? Tests { get; set; }
        public DateTime Timestamp { get; set; }
    }

    private sealed class ResumeResultLine
    {
        public string Type { get; set; } = "result";
        public string AssemblyPath { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string Test { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public DateTime Timestamp { get; set; }
    }
}

internal sealed class ResumeEntry
{
    public string Test { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}
