using System.Xml.Linq;
using Asynkron.TestRunner.Models;

namespace Asynkron.TestRunner;

public static class TrxParser
{
    private static readonly XNamespace TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public static TestRunResult? ParseTrxFile(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            var doc = XDocument.Load(filePath);
            var root = doc.Root;
            if (root == null)
                return null;

            var resultSummary = root.Element(TrxNamespace + "ResultSummary");
            var counters = resultSummary?.Element(TrxNamespace + "Counters");

            if (counters == null)
                return null;

            var passed = int.Parse(counters.Attribute("passed")?.Value ?? "0");
            var failed = int.Parse(counters.Attribute("failed")?.Value ?? "0");
            var skipped = int.Parse(counters.Attribute("notExecuted")?.Value ?? "0");

            // Extract individual test results
            var (passedTests, failedTests, timedOutTests) = ExtractTestNames(root);

            // Get timing info
            var times = root.Element(TrxNamespace + "Times");
            var duration = TimeSpan.Zero;
            if (times != null)
            {
                var start = times.Attribute("start")?.Value;
                var finish = times.Attribute("finish")?.Value;
                if (start != null && finish != null)
                {
                    if (DateTime.TryParse(start, out var startTime) &&
                        DateTime.TryParse(finish, out var finishTime))
                    {
                        duration = finishTime - startTime;
                    }
                }
            }

            return new TestRunResult
            {
                Id = Path.GetFileNameWithoutExtension(filePath),
                Timestamp = File.GetCreationTime(filePath),
                Passed = passed,
                Failed = failed,
                Skipped = skipped,
                Duration = duration,
                TrxFilePath = filePath,
                PassedTests = passedTests,
                FailedTests = failedTests,
                TimedOutTests = timedOutTests
            };
        }
        catch
        {
            return null;
        }
    }

    private static (List<string> Passed, List<string> Failed, List<string> TimedOut) ExtractTestNames(XElement root)
    {
        var passed = new List<string>();
        var failed = new List<string>();
        var timedOut = new List<string>();

        var results = root.Element(TrxNamespace + "Results");
        if (results == null)
            return (passed, failed, timedOut);

        foreach (var result in results.Elements(TrxNamespace + "UnitTestResult"))
        {
            var testName = result.Attribute("testName")?.Value;
            var outcome = result.Attribute("outcome")?.Value;

            if (string.IsNullOrEmpty(testName))
                continue;

            switch (outcome?.ToLowerInvariant())
            {
                case "passed":
                    passed.Add(testName);
                    break;
                case "failed":
                    // Check if it's a timeout failure
                    var errorMessage = result
                        .Element(TrxNamespace + "Output")?
                        .Element(TrxNamespace + "ErrorInfo")?
                        .Element(TrxNamespace + "Message")?.Value ?? "";

                    if (IsTimeoutFailure(errorMessage))
                    {
                        timedOut.Add(testName);
                    }
                    else
                    {
                        failed.Add(testName);
                    }
                    break;
            }
        }

        return (passed, failed, timedOut);
    }

    private static bool IsTimeoutFailure(string errorMessage)
    {
        var lowerMessage = errorMessage.ToLowerInvariant();
        return lowerMessage.Contains("timed out") ||
               lowerMessage.Contains("timeout") ||
               lowerMessage.Contains("hang") ||
               lowerMessage.Contains("exceeded") ||
               lowerMessage.Contains("did not complete");
    }

    public static TestRunResult? ParseFromDirectory(string trxDirectory)
    {
        if (!Directory.Exists(trxDirectory))
            return null;

        var trxFiles = Directory.GetFiles(trxDirectory, "*.trx");
        if (trxFiles.Length == 0)
            return null;

        // Aggregate results from multiple TRX files (parallel test runs)
        var results = trxFiles
            .Select(ParseTrxFile)
            .Where(r => r != null)
            .ToList();

        if (results.Count == 0)
            return null;

        // Combine results
        return new TestRunResult
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Timestamp = results.Min(r => r!.Timestamp),
            Passed = results.Sum(r => r!.Passed),
            Failed = results.Sum(r => r!.Failed),
            Skipped = results.Sum(r => r!.Skipped),
            Duration = TimeSpan.FromTicks(results.Max(r => r!.Duration.Ticks)),
            TrxFilePath = trxDirectory,
            PassedTests = results.SelectMany(r => r!.PassedTests).ToList(),
            FailedTests = results.SelectMany(r => r!.FailedTests).ToList(),
            TimedOutTests = results.SelectMany(r => r!.TimedOutTests).ToList()
        };
    }
}
