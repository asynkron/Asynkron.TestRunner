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

    public string StoreFolder => _storeFolder;
    public string BaseFolder => _baseFolder;
    public string CommandSignature => _commandSignature;

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
                    return dir;
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
        if (!File.Exists(_historyFilePath))
            return new List<TestRunResult>();

        try
        {
            var json = File.ReadAllText(_historyFilePath);
            return JsonSerializer.Deserialize<List<TestRunResult>>(json, GetJsonOptions())
                   ?? new List<TestRunResult>();
        }
        catch
        {
            return new List<TestRunResult>();
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
