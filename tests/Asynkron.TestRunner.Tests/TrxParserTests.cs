using Asynkron.TestRunner;
using Asynkron.TestRunner.Models;
using Xunit;

namespace Asynkron.TestRunner.Tests;

public class TrxParserTests
{
    private const string ValidTrxContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="test-run-id" name="Test Run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Times creation="2024-01-01T10:00:00.0000000+00:00" start="2024-01-01T10:00:00.0000000+00:00" finish="2024-01-01T10:00:05.0000000+00:00" />
          <ResultSummary outcome="Completed">
            <Counters total="5" executed="5" passed="3" failed="1" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="1" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
          </ResultSummary>
          <Results>
            <UnitTestResult testId="1" testName="Test1" outcome="Passed" />
            <UnitTestResult testId="2" testName="Test2" outcome="Passed" />
            <UnitTestResult testId="3" testName="Test3" outcome="Passed" />
            <UnitTestResult testId="4" testName="Test4" outcome="Failed">
              <Output>
                <ErrorInfo>
                  <Message>Assertion failed</Message>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testId="5" testName="Test5" outcome="NotExecuted" />
          </Results>
        </TestRun>
        """;

    private const string TimeoutTrxContent = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="test-run-id" name="Test Run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Times creation="2024-01-01T10:00:00.0000000+00:00" start="2024-01-01T10:00:00.0000000+00:00" finish="2024-01-01T10:00:30.0000000+00:00" />
          <ResultSummary outcome="Failed">
            <Counters total="2" executed="2" passed="1" failed="1" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
          </ResultSummary>
          <Results>
            <UnitTestResult testId="1" testName="PassingTest" outcome="Passed" />
            <UnitTestResult testId="2" testName="HangingTest" outcome="Failed">
              <Output>
                <ErrorInfo>
                  <Message>Test timed out after 30000ms</Message>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
          </Results>
        </TestRun>
        """;

    [Fact]
    public void ParseTrxFile_ValidFile_ReturnsCorrectCounts()
    {
        var tempFile = CreateTempTrxFile(ValidTrxContent);
        try
        {
            var result = TrxParser.ParseTrxFile(tempFile);

            Assert.NotNull(result);
            Assert.Equal(3, result.Passed);
            Assert.Equal(1, result.Failed);
            Assert.Equal(1, result.Skipped);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseTrxFile_ValidFile_ExtractsPassedTestNames()
    {
        var tempFile = CreateTempTrxFile(ValidTrxContent);
        try
        {
            var result = TrxParser.ParseTrxFile(tempFile);

            Assert.NotNull(result);
            Assert.Equal(3, result.PassedTests.Count);
            Assert.Contains("Test1", result.PassedTests);
            Assert.Contains("Test2", result.PassedTests);
            Assert.Contains("Test3", result.PassedTests);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseTrxFile_ValidFile_ExtractsFailedTestNames()
    {
        var tempFile = CreateTempTrxFile(ValidTrxContent);
        try
        {
            var result = TrxParser.ParseTrxFile(tempFile);

            Assert.NotNull(result);
            Assert.Single(result.FailedTests);
            Assert.Contains("Test4", result.FailedTests);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseTrxFile_TimeoutTests_IdentifiesTimedOutTests()
    {
        var tempFile = CreateTempTrxFile(TimeoutTrxContent);
        try
        {
            var result = TrxParser.ParseTrxFile(tempFile);

            Assert.NotNull(result);
            Assert.Single(result.TimedOutTests);
            Assert.Contains("HangingTest", result.TimedOutTests);
            Assert.Empty(result.FailedTests); // Should not be in failed since it's a timeout
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseTrxFile_NonExistentFile_ReturnsNull()
    {
        var result = TrxParser.ParseTrxFile("/nonexistent/path/file.trx");
        Assert.Null(result);
    }

    [Fact]
    public void ParseTrxFile_InvalidXml_ReturnsNull()
    {
        var tempFile = CreateTempTrxFile("not valid xml content");
        try
        {
            var result = TrxParser.ParseTrxFile(tempFile);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseFromDirectory_MultipleFiles_DeduplicatesResults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Same content - tests should be deduplicated
            File.WriteAllText(Path.Combine(tempDir, "test1.trx"), ValidTrxContent);
            File.WriteAllText(Path.Combine(tempDir, "test2.trx"), ValidTrxContent);

            var result = TrxParser.ParseFromDirectory(tempDir);

            Assert.NotNull(result);
            // Should be deduplicated: Test1, Test2, Test3 passed; Test4 failed
            Assert.Equal(3, result.Passed);
            Assert.Equal(1, result.Failed);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ParseFromDirectory_EmptyDirectory_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var result = TrxParser.ParseFromDirectory(tempDir);
            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ParseFromDirectory_NonExistentDirectory_ReturnsNull()
    {
        var result = TrxParser.ParseFromDirectory("/nonexistent/directory");
        Assert.Null(result);
    }

    [Fact]
    public void ParseTrxFile_CalculatesDuration()
    {
        var tempFile = CreateTempTrxFile(ValidTrxContent);
        try
        {
            var result = TrxParser.ParseTrxFile(tempFile);

            Assert.NotNull(result);
            Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static string CreateTempTrxFile(string content)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.trx");
        File.WriteAllText(tempFile, content);
        return tempFile;
    }

    // MergeResults tests
    private static TestRunResult CreateTestResult(
        string id = "1",
        int passed = 0,
        int failed = 0,
        int skipped = 0,
        TimeSpan? duration = null,
        DateTime? timestamp = null,
        List<string>? passedTests = null,
        List<string>? failedTests = null,
        List<string>? timedOutTests = null)
    {
        return new TestRunResult
        {
            Id = id,
            Timestamp = timestamp ?? DateTime.Now,
            Passed = passed,
            Failed = failed,
            Skipped = skipped,
            Duration = duration ?? TimeSpan.Zero,
            PassedTests = passedTests ?? [],
            FailedTests = failedTests ?? [],
            TimedOutTests = timedOutTests ?? []
        };
    }

    [Fact]
    public void MergeResults_SingleResult_ReturnsSameResult()
    {
        var result = CreateTestResult(
            passed: 5,
            failed: 2,
            skipped: 1,
            duration: TimeSpan.FromSeconds(10),
            passedTests: ["Test1", "Test2"],
            failedTests: ["Test3"],
            timedOutTests: ["Test4"]);

        var merged = TrxParser.MergeResults([result]);

        Assert.Equal(result, merged);
    }

    [Fact]
    public void MergeResults_DuplicatePassedTests_Deduplicated()
    {
        var result1 = CreateTestResult(
            id: "1",
            passed: 2,
            passedTests: ["Test1", "Test2"]);
        var result2 = CreateTestResult(
            id: "2",
            passed: 2,
            passedTests: ["Test2", "Test3"]);

        var merged = TrxParser.MergeResults([result1, result2]);

        Assert.Equal(3, merged.Passed);
        Assert.Contains("Test1", merged.PassedTests);
        Assert.Contains("Test2", merged.PassedTests);
        Assert.Contains("Test3", merged.PassedTests);
    }

    [Fact]
    public void MergeResults_PassedOverridesFailedOnRetry()
    {
        // Test that initially failed but passed on retry should be counted as passed
        var result1 = CreateTestResult(
            id: "1",
            passed: 1,
            failed: 1,
            passedTests: ["Test1"],
            failedTests: ["Test2"]); // Initially failed
        var result2 = CreateTestResult(
            id: "2",
            passed: 1,
            timestamp: DateTime.Now.AddSeconds(1),
            passedTests: ["Test2"]); // Passed on retry

        var merged = TrxParser.MergeResults([result1, result2]);

        Assert.Equal(2, merged.Passed);
        Assert.Empty(merged.FailedTests);
        Assert.Contains("Test1", merged.PassedTests);
        Assert.Contains("Test2", merged.PassedTests);
    }

    [Fact]
    public void MergeResults_PassedOverridesTimedOutOnRetry()
    {
        // Test that initially timed out but passed on retry should be counted as passed
        var result1 = CreateTestResult(
            id: "1",
            timedOutTests: ["Test1"]); // Initially timed out
        var result2 = CreateTestResult(
            id: "2",
            passed: 1,
            timestamp: DateTime.Now.AddSeconds(1),
            passedTests: ["Test1"]); // Passed on retry

        var merged = TrxParser.MergeResults([result1, result2]);

        Assert.Single(merged.PassedTests);
        Assert.Contains("Test1", merged.PassedTests);
        Assert.Empty(merged.TimedOutTests);
    }

    [Fact]
    public void MergeResults_FailedOverridesTimedOut()
    {
        // Failed is more informative than timed out
        var result1 = CreateTestResult(
            id: "1",
            timedOutTests: ["Test1"]);
        var result2 = CreateTestResult(
            id: "2",
            failed: 1,
            timestamp: DateTime.Now.AddSeconds(1),
            failedTests: ["Test1"]); // Failed with actual error

        var merged = TrxParser.MergeResults([result1, result2]);

        Assert.Single(merged.FailedTests);
        Assert.Contains("Test1", merged.FailedTests);
        Assert.Empty(merged.TimedOutTests);
    }

    [Fact]
    public void MergeResults_CaseInsensitiveTestNames()
    {
        var result1 = CreateTestResult(
            id: "1",
            passed: 1,
            passedTests: ["test1"]);
        var result2 = CreateTestResult(
            id: "2",
            passed: 1,
            passedTests: ["TEST1"]);

        var merged = TrxParser.MergeResults([result1, result2]);

        // Should be deduplicated (case-insensitive)
        Assert.Single(merged.PassedTests);
    }

    [Fact]
    public void MergeResults_TakesDurationFromLongestRun()
    {
        var result1 = CreateTestResult(
            id: "1",
            duration: TimeSpan.FromSeconds(5));
        var result2 = CreateTestResult(
            id: "2",
            duration: TimeSpan.FromSeconds(15));

        var merged = TrxParser.MergeResults([result1, result2]);

        Assert.Equal(TimeSpan.FromSeconds(15), merged.Duration);
    }

    [Fact]
    public void MergeResults_TakesEarliestTimestamp()
    {
        var earlier = DateTime.Now.AddMinutes(-5);
        var later = DateTime.Now;

        var result1 = CreateTestResult(id: "1", timestamp: later);
        var result2 = CreateTestResult(id: "2", timestamp: earlier);

        var merged = TrxParser.MergeResults([result1, result2]);

        Assert.Equal(earlier, merged.Timestamp);
    }

    [Fact]
    public void MergeResults_EmptyList_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => TrxParser.MergeResults([]));
    }
}
