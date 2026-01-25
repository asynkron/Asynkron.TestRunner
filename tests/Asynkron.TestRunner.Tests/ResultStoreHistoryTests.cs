using System.Text.Json;
using Asynkron.TestRunner;
using Asynkron.TestRunner.Models;
using Xunit;

namespace Asynkron.TestRunner.Tests;

public class ResultStoreHistoryTests
{
    [Fact]
    public void FindLatestHistoryFile_PicksMostRecentlyWrittenHistoryJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "Asynkron.TestRunner.Tests", Guid.NewGuid().ToString("N"));
        var baseFolder = Path.Combine(root, ".testrunner");
        var projectFolder = Path.Combine(baseFolder, "projhash");
        var commandA = Path.Combine(projectFolder, "aaaa");
        var commandB = Path.Combine(projectFolder, "bbbb");

        Directory.CreateDirectory(commandA);
        Directory.CreateDirectory(commandB);

        var fileA = Path.Combine(commandA, "history.json");
        var fileB = Path.Combine(commandB, "history.json");

        File.WriteAllText(fileA, "[]");
        File.WriteAllText(fileB, "[]");

        File.SetLastWriteTimeUtc(fileA, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(fileB, DateTime.UtcNow.AddMinutes(-1));

        try
        {
            var store = ResultStore.FromHistoryFile(fileA);
            var latest = store.FindLatestHistoryFile();

            Assert.Equal(Path.GetFullPath(fileB), Path.GetFullPath(latest!));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void GetLatestRun_ReturnsMostRecentTimestampEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "Asynkron.TestRunner.Tests", Guid.NewGuid().ToString("N"));
        var historyFile = Path.Combine(root, ".testrunner", "projhash", "aaaa", "history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);

        var older = new TestRunResult
        {
            Id = "older",
            Timestamp = DateTime.UtcNow.AddMinutes(-5),
            Passed = 1,
            Failed = 0,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(1),
            PassedTests = ["A"],
        };

        var newer = new TestRunResult
        {
            Id = "newer",
            Timestamp = DateTime.UtcNow,
            Passed = 0,
            Failed = 1,
            Skipped = 0,
            Duration = TimeSpan.FromSeconds(2),
            FailedTests = ["B"],
        };

        var json = JsonSerializer.Serialize(
            new List<TestRunResult> { older, newer },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(historyFile, json);

        try
        {
            var latest = ResultStore.GetLatestRun(historyFile);
            Assert.NotNull(latest);
            Assert.Equal("newer", latest!.Id);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
