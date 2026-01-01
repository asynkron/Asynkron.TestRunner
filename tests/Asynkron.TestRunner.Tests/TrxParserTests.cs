using Xunit;

namespace Asynkron.TestRunner.Tests;

public class TrxParserTests
{
    private readonly string _testDataDir;

    public TrxParserTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), $"trx_parser_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataDir);
    }

    [Fact]
    public void ParseTrxFile_ValidFile_ReturnsResult()
    {
        var trxPath = CreateSampleTrxFile(passed: 5, failed: 2);
        var result = TrxParser.ParseTrxFile(trxPath);

        Assert.NotNull(result);
        Assert.Equal(5, result.Passed);
        Assert.Equal(2, result.Failed);
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
        var filePath = Path.Combine(_testDataDir, "invalid.trx");
        File.WriteAllText(filePath, "this is not xml");

        var result = TrxParser.ParseTrxFile(filePath);
        Assert.Null(result);
    }

    [Fact]
    public void ParseFromDirectory_MultipleTrxFiles_AggregatesResults()
    {
        CreateSampleTrxFile(passed: 3, failed: 1, fileName: "result1.trx");
        CreateSampleTrxFile(passed: 4, failed: 2, fileName: "result2.trx");

        var result = TrxParser.ParseFromDirectory(_testDataDir);

        Assert.NotNull(result);
        Assert.Equal(7, result.Passed);
        Assert.Equal(3, result.Failed);
    }

    [Fact]
    public void ParseFromDirectory_EmptyDirectory_ReturnsNull()
    {
        var emptyDir = Path.Combine(_testDataDir, "empty");
        Directory.CreateDirectory(emptyDir);

        var result = TrxParser.ParseFromDirectory(emptyDir);
        Assert.Null(result);
    }

    [Fact]
    public void ParseTrxFile_ExtractsTestNames()
    {
        var trxPath = CreateTrxFileWithTestNames(
            passed: ["Namespace.Class.PassingTest"],
            failed: ["Namespace.Class.FailingTest"]
        );

        var result = TrxParser.ParseTrxFile(trxPath);

        Assert.NotNull(result);
        Assert.Contains("Namespace.Class.PassingTest", result.PassedTests);
        Assert.Contains("Namespace.Class.FailingTest", result.FailedTests);
    }

    [Fact]
    public void ParseTrxFile_DetectsTimeoutTests()
    {
        var trxPath = CreateTrxFileWithTimeout("Namespace.Class.SlowTest");

        var result = TrxParser.ParseTrxFile(trxPath);

        Assert.NotNull(result);
        Assert.Contains("Namespace.Class.SlowTest", result.TimedOutTests);
        Assert.DoesNotContain("Namespace.Class.SlowTest", result.FailedTests);
    }

    private string CreateSampleTrxFile(int passed, int failed, string? fileName = null)
    {
        fileName ??= $"result_{Guid.NewGuid():N}.trx";
        var filePath = Path.Combine(_testDataDir, fileName);

        var trxContent = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="{passed + failed}" passed="{passed}" failed="{failed}" notExecuted="0" />
              </ResultSummary>
              <Times start="2024-01-01T00:00:00" finish="2024-01-01T00:01:00" />
              <Results>
              </Results>
            </TestRun>
            """;

        File.WriteAllText(filePath, trxContent);
        return filePath;
    }

    private string CreateTrxFileWithTestNames(string[] passed, string[] failed)
    {
        var filePath = Path.Combine(_testDataDir, $"result_{Guid.NewGuid():N}.trx");

        var passedResults = string.Join("\n", passed.Select(t =>
            $"""<UnitTestResult testName="{t}" outcome="Passed" />"""));
        var failedResults = string.Join("\n", failed.Select(t =>
            $"""<UnitTestResult testName="{t}" outcome="Failed"><Output><ErrorInfo><Message>Assert failed</Message></ErrorInfo></Output></UnitTestResult>"""));

        var trxContent = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="{passed.Length + failed.Length}" passed="{passed.Length}" failed="{failed.Length}" notExecuted="0" />
              </ResultSummary>
              <Times start="2024-01-01T00:00:00" finish="2024-01-01T00:01:00" />
              <Results>
                {passedResults}
                {failedResults}
              </Results>
            </TestRun>
            """;

        File.WriteAllText(filePath, trxContent);
        return filePath;
    }

    private string CreateTrxFileWithTimeout(string testName)
    {
        var filePath = Path.Combine(_testDataDir, $"result_{Guid.NewGuid():N}.trx");

        var trxContent = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="1" passed="0" failed="1" notExecuted="0" />
              </ResultSummary>
              <Times start="2024-01-01T00:00:00" finish="2024-01-01T00:01:00" />
              <Results>
                <UnitTestResult testName="{testName}" outcome="Failed">
                  <Output>
                    <ErrorInfo>
                      <Message>Test timed out after 30 seconds</Message>
                    </ErrorInfo>
                  </Output>
                </UnitTestResult>
              </Results>
            </TestRun>
            """;

        File.WriteAllText(filePath, trxContent);
        return filePath;
    }
}
