using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asynkron.TestRunner.Models;

namespace Asynkron.TestRunner;

public class ResultStore
{
    private const string StoreFolderName = ".testrunner";
    private const string HistoryFileName = "history.json";
    private const int MaxHistoryCount = 50;

    private readonly string _baseFolder;
    private readonly string _storeFolder;
    private readonly string _historyFilePath;
    private readonly string _commandSignature;

    public ResultStore(string[]? commandArgs = null)
    {
        var projectRoot = GetProjectRoot();
        var projectHash = ComputeShortHash(projectRoot);
        var commandHash = ComputeShortHash(string.Join(" ", commandArgs ?? []));

        _commandSignature = string.Join(" ", commandArgs ?? ["(default)"]);
        _baseFolder = Path.Combine(projectRoot, StoreFolderName);
        _storeFolder = Path.Combine(_baseFolder, projectHash, commandHash);
        _historyFilePath = Path.Combine(_storeFolder, HistoryFileName);
    }

    private ResultStore(string baseFolder, string storeFolder, string historyFilePath, string commandSignature)
    {
        _baseFolder = baseFolder;
        _storeFolder = storeFolder;
        _historyFilePath = historyFilePath;
        _commandSignature = commandSignature;
    }

    public string StoreFolder => _storeFolder;
    public string BaseFolder => _baseFolder;
    public string CommandSignature => _commandSignature;
    public string HistoryFilePath => _historyFilePath;

    /// <summary>
    /// The project-specific store folder: &lt;repo&gt;/.testrunner/&lt;projectHash&gt;
    /// </summary>
    public string ProjectFolder => Directory.GetParent(_storeFolder)?.FullName ?? _storeFolder;

    public static ResultStore FromHistoryFile(string historyFilePath)
    {
        if (string.IsNullOrWhiteSpace(historyFilePath))
        {
            throw new ArgumentException("History file path is required", nameof(historyFilePath));
        }

        var fullPath = Path.GetFullPath(historyFilePath);
        var storeFolder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(storeFolder))
        {
            throw new ArgumentException("Invalid history file path", nameof(historyFilePath));
        }

        var projectFolder = Directory.GetParent(storeFolder)?.FullName;
        if (string.IsNullOrWhiteSpace(projectFolder))
        {
            throw new ArgumentException("History file path must be inside a .testrunner project folder", nameof(historyFilePath));
        }

        var baseFolder = Directory.GetParent(projectFolder)?.FullName;
        if (string.IsNullOrWhiteSpace(baseFolder))
        {
            throw new ArgumentException("History file path must be inside a .testrunner store folder", nameof(historyFilePath));
        }

        var commandSignature = $"(history:{Path.GetFileName(storeFolder)})";
        return new ResultStore(baseFolder, storeFolder, fullPath, commandSignature);
    }

    public void EnsureStoreExists()
    {
        if (!Directory.Exists(_storeFolder))
        {
            Directory.CreateDirectory(_storeFolder);
        }
    }

    private static string GetProjectRoot()
    {
        // Try to find git root
        try
        {
            var dir = Directory.GetCurrentDirectory();
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                {
                    return dir;
                }

                dir = Directory.GetParent(dir)?.FullName;
            }
        }
        catch { }

        // Fall back to current directory
        return Directory.GetCurrentDirectory();
    }

    private static string ComputeShortHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    public List<TestRunResult> LoadHistory()
    {
        return LoadHistoryFile(_historyFilePath);
    }

    public static List<TestRunResult> LoadHistoryFile(string historyFilePath)
    {
        if (!File.Exists(historyFilePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(historyFilePath);
            return JsonSerializer.Deserialize<List<TestRunResult>>(json, GetJsonOptions())
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveResult(TestRunResult result)
    {
        EnsureStoreExists();

        var history = LoadHistory();
        history.Add(result);

        // Keep only recent runs
        if (history.Count > MaxHistoryCount)
        {
            var toRemove = history
                .OrderBy(r => r.Timestamp)
                .Take(history.Count - MaxHistoryCount)
                .ToList();

            foreach (var old in toRemove)
            {
                history.Remove(old);
                // Clean up old result directories
                if (old.TrxFilePath != null && Directory.Exists(old.TrxFilePath))
                {
                    try { Directory.Delete(old.TrxFilePath, recursive: true); } catch { }
                }
            }
        }

        var json = JsonSerializer.Serialize(history, GetJsonOptions());
        File.WriteAllText(_historyFilePath, json);
    }

    public string? FindLatestHistoryFile()
    {
        if (!Directory.Exists(ProjectFolder))
        {
            return null;
        }

        string? latestFile = null;
        var latestWrite = DateTime.MinValue;

        foreach (var dir in Directory.EnumerateDirectories(ProjectFolder))
        {
            var file = Path.Combine(dir, HistoryFileName);
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var write = File.GetLastWriteTimeUtc(file);
                if (latestFile == null || write > latestWrite)
                {
                    latestFile = file;
                    latestWrite = write;
                }
            }
            catch
            {
                // ignore
            }
        }

        return latestFile;
    }

    public static TestRunResult? GetLatestRun(string historyFilePath)
    {
        return LoadHistoryFile(historyFilePath)
            .OrderByDescending(r => r.Timestamp)
            .FirstOrDefault();
    }

    public List<TestRunResult> GetRecentRuns(int count)
    {
        return LoadHistory()
            .OrderByDescending(r => r.Timestamp)
            .Take(count)
            .ToList();
    }

    private static JsonSerializerOptions GetJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
