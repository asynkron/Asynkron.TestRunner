using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Xml;
using NUnit.Engine;

namespace Asynkron.TestRunner.Worker;

/// <summary>
/// NUnit test framework implementation
/// </summary>
public class NUnitFramework : ITestFramework
{
    public bool CanHandle(string assemblyPath)
    {
        try
        {
            var assemblyDir = Path.GetDirectoryName(assemblyPath)!;
            var nunitFramework = Path.Combine(assemblyDir, "nunit.framework.dll");

            return File.Exists(nunitFramework);
        }
        catch
        {
            return false;
        }
    }

    public IEnumerable<TestInfo> Discover(string assemblyPath)
    {
        using var engine = TestEngineActivator.CreateInstance();
        var package = new TestPackage(assemblyPath);

        using var runner = engine.GetRunner(package);
        var testsXml = runner.Explore(TestFilter.Empty);

        return ParseTestCases(testsXml);
    }

    public async IAsyncEnumerable<TestResult> RunAsync(
        string assemblyPath,
        IEnumerable<string>? testFqns,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<TestResult>();

        var runTask = Task.Run(() =>
        {
            try
            {
                using var engine = TestEngineActivator.CreateInstance();
                var package = new TestPackage(assemblyPath);

                using var runner = engine.GetRunner(package);

                // Build filter if specific tests requested
                var filter = BuildFilter(testFqns);

                // Create event handler
                var handler = new TestEventHandler(channel.Writer, ct);

                // Run tests - results are streamed via TestEventHandler
                runner.Run(handler, filter);
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new TestFailed("", "Worker Error", TimeSpan.Zero, ex.Message, ex.StackTrace));
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        await foreach (var result in channel.Reader.ReadAllAsync(ct))
        {
            yield return result;
        }

        await runTask;
    }

    private static TestFilter BuildFilter(IEnumerable<string>? testFqns)
    {
        if (testFqns == null)
            return TestFilter.Empty;

        var fqnList = testFqns.ToList();
        if (fqnList.Count == 0)
            return TestFilter.Empty;

        // NUnit filter format: <filter><or><test>FQN1</test><test>FQN2</test></or></filter>
        var filterBuilder = new System.Text.StringBuilder("<filter>");

        if (fqnList.Count == 1)
        {
            filterBuilder.Append($"<test>{EscapeXml(fqnList[0])}</test>");
        }
        else
        {
            filterBuilder.Append("<or>");
            foreach (var fqn in fqnList)
            {
                filterBuilder.Append($"<test>{EscapeXml(fqn)}</test>");
            }
            filterBuilder.Append("</or>");
        }

        filterBuilder.Append("</filter>");

        return new TestFilter(filterBuilder.ToString());
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static IEnumerable<TestInfo> ParseTestCases(XmlNode node)
    {
        var testCases = node.SelectNodes("//test-case");
        if (testCases == null)
            yield break;

        foreach (XmlNode testCase in testCases)
        {
            var fullname = testCase.Attributes?["fullname"]?.Value ?? "";
            var name = testCase.Attributes?["name"]?.Value ?? fullname;
            var runstate = testCase.Attributes?["runstate"]?.Value;

            string? skipReason = null;
            if (runstate == "Ignored" || runstate == "Skipped")
            {
                var reasonNode = testCase.SelectSingleNode("properties/property[@name='_SKIPREASON']");
                skipReason = reasonNode?.Attributes?["value"]?.Value ?? "Ignored";
            }

            yield return new TestInfo(fullname, name, skipReason);
        }
    }

    private static void ParseResults(XmlNode resultXml, ChannelWriter<TestResult> writer)
    {
        var testCases = resultXml.SelectNodes("//test-case");
        if (testCases == null)
            return;

        foreach (XmlNode testCase in testCases)
        {
            var fullname = testCase.Attributes?["fullname"]?.Value ?? "";
            var name = testCase.Attributes?["name"]?.Value ?? fullname;
            var result = testCase.Attributes?["result"]?.Value ?? "";
            var durationStr = testCase.Attributes?["duration"]?.Value ?? "0";

            if (!double.TryParse(durationStr, out var durationSecs))
                durationSecs = 0;

            var duration = TimeSpan.FromSeconds(durationSecs);

            switch (result.ToLowerInvariant())
            {
                case "passed":
                    writer.TryWrite(new TestPassed(fullname, name, duration));
                    break;

                case "failed":
                    var failureNode = testCase.SelectSingleNode("failure");
                    var message = failureNode?.SelectSingleNode("message")?.InnerText ?? "Test failed";
                    var stackTrace = failureNode?.SelectSingleNode("stack-trace")?.InnerText;
                    writer.TryWrite(new TestFailed(fullname, name, duration, message, stackTrace));
                    break;

                case "skipped":
                case "ignored":
                    var reasonNode = testCase.SelectSingleNode("reason/message");
                    var reason = reasonNode?.InnerText;
                    writer.TryWrite(new TestSkipped(fullname, name, reason));
                    break;
            }
        }
    }

    private class TestEventHandler : ITestEventListener
    {
        private readonly ChannelWriter<TestResult> _writer;
        private readonly CancellationToken _ct;

        public TestEventHandler(ChannelWriter<TestResult> writer, CancellationToken ct)
        {
            _writer = writer;
            _ct = ct;
        }

        public void OnTestEvent(string report)
        {
            if (_ct.IsCancellationRequested)
                return;

            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(report);
                var root = doc.DocumentElement;

                if (root == null)
                    return;

                switch (root.Name)
                {
                    case "start-test":
                        var startFullname = root.Attributes?["fullname"]?.Value ?? "";
                        var startName = root.Attributes?["name"]?.Value ?? startFullname;
                        _writer.TryWrite(new TestStarted(startFullname, startName));
                        break;

                    case "test-case":
                        HandleTestCase(root);
                        break;

                    case "test-output":
                        var outFullname = root.Attributes?["testname"]?.Value ?? "";
                        var output = root.InnerText;
                        if (!string.IsNullOrEmpty(output))
                            _writer.TryWrite(new TestOutput(outFullname, output));
                        break;
                }
            }
            catch
            {
                // Ignore malformed events
            }
        }

        private void HandleTestCase(XmlElement root)
        {
            var fullname = root.Attributes?["fullname"]?.Value ?? "";
            var name = root.Attributes?["name"]?.Value ?? fullname;
            var result = root.Attributes?["result"]?.Value ?? "";
            var durationStr = root.Attributes?["duration"]?.Value ?? "0";

            if (!double.TryParse(durationStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var durationSecs))
                durationSecs = 0;

            var duration = TimeSpan.FromSeconds(durationSecs);

            switch (result.ToLowerInvariant())
            {
                case "passed":
                    _writer.TryWrite(new TestPassed(fullname, name, duration));
                    break;

                case "failed":
                    var failureNode = root.SelectSingleNode("failure");
                    var message = failureNode?.SelectSingleNode("message")?.InnerText ?? "Test failed";
                    var stackTrace = failureNode?.SelectSingleNode("stack-trace")?.InnerText;
                    _writer.TryWrite(new TestFailed(fullname, name, duration, message, stackTrace));
                    break;

                case "skipped":
                case "ignored":
                    var reasonNode = root.SelectSingleNode("reason/message");
                    var reason = reasonNode?.InnerText;
                    _writer.TryWrite(new TestSkipped(fullname, name, reason));
                    break;
            }
        }
    }
}
