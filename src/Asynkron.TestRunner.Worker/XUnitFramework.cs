using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading.Channels;
using Xunit;
using Xunit.Abstractions;

namespace Asynkron.TestRunner.Worker;

/// <summary>
/// xUnit test framework implementation
/// </summary>
public class XUnitFramework : ITestFramework
{
    public bool CanHandle(string assemblyPath)
    {
        // Check if assembly references xunit - require xunit.core.dll (not just abstractions)
        try
        {
            var assemblyDir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
            var xunitCore = Path.Combine(assemblyDir, "xunit.core.dll");

            // xunit.abstractions alone is not enough (can be transitive dep from NUnit)
            return File.Exists(xunitCore);
        }
        catch
        {
            return false;
        }
    }

    public IEnumerable<TestInfo> Discover(string assemblyPath)
    {
        // Ensure absolute path
        assemblyPath = Path.GetFullPath(assemblyPath);

        // Set up assembly resolver for test assembly directory
        var assemblyDir = Path.GetDirectoryName(assemblyPath)!;
        using var resolver = new TestAssemblyResolver(assemblyDir);

        using var controller = new XunitFrontController(
            AppDomainSupport.Denied,
            assemblyPath,
            diagnosticMessageSink: new NullMessageSink());

        var discoveryOptions = TestFrameworkOptions.ForDiscovery();
        var sink = new DiscoverySink();

        controller.Find(includeSourceInformation: false, messageSink: sink, discoveryOptions: discoveryOptions);
        sink.Finished.WaitOne();

        return sink.Tests;
    }

    public async IAsyncEnumerable<TestResult> RunAsync(
        string assemblyPath,
        IEnumerable<string>? testFqns,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Ensure absolute path
        assemblyPath = Path.GetFullPath(assemblyPath);
        var assemblyDir = Path.GetDirectoryName(assemblyPath)!;

        var channel = Channel.CreateUnbounded<TestResult>();

        var runTask = Task.Run(() =>
        {
            try
            {
                // Set up assembly resolver for test assembly directory
                using var resolver = new TestAssemblyResolver(assemblyDir);

                using var controller = new XunitFrontController(
                    AppDomainSupport.Denied,
                    assemblyPath,
                    diagnosticMessageSink: new NullMessageSink());

                // Discover tests first
                var discoveryOptions = TestFrameworkOptions.ForDiscovery();
                var discoverySink = new DiscoverySink();
                controller.Find(includeSourceInformation: false, messageSink: discoverySink, discoveryOptions: discoveryOptions);
                discoverySink.Finished.WaitOne();

                // Filter tests if specified
                var testCases = discoverySink.TestCases.AsEnumerable();
                if (testFqns != null)
                {
                    var fqnSet = new HashSet<string>(testFqns, StringComparer.OrdinalIgnoreCase);
                    testCases = testCases.Where(tc =>
                        fqnSet.Contains(tc.TestMethod.TestClass.Class.Name + "." + tc.TestMethod.Method.Name) ||
                        fqnSet.Contains(tc.TestMethod.TestClass.Class.Name.Split('.').Last() + "." + tc.TestMethod.Method.Name));
                }

                var testCasesList = testCases.ToList();
                if (testCasesList.Count == 0)
                {
                    channel.Writer.Complete();
                    return;
                }

                // Run tests
                var executionOptions = TestFrameworkOptions.ForExecution();
                var executionSink = new ExecutionSink(channel.Writer, ct);

                controller.RunTests(testCasesList, executionSink, executionOptions);
                executionSink.Finished.WaitOne();
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

    private class NullMessageSink : IMessageSink
    {
        public bool OnMessage(IMessageSinkMessage message) => true;
    }

    private class DiscoverySink : IMessageSink
    {
        public List<TestInfo> Tests { get; } = new();
        public List<ITestCase> TestCases { get; } = new();
        public ManualResetEvent Finished { get; } = new(false);

        public bool OnMessage(IMessageSinkMessage message)
        {
            switch (message)
            {
                case ITestCaseDiscoveryMessage discovery:
                    var testCase = discovery.TestCase;
                    var fqn = $"{testCase.TestMethod.TestClass.Class.Name}.{testCase.TestMethod.Method.Name}";
                    var displayName = testCase.DisplayName;
                    var skipReason = testCase.SkipReason;

                    Tests.Add(new TestInfo(fqn, displayName, skipReason));
                    TestCases.Add(testCase);
                    break;

                case IDiscoveryCompleteMessage:
                    Finished.Set();
                    break;
            }

            return true;
        }
    }

    private class ExecutionSink : IMessageSink
    {
        private readonly ChannelWriter<TestResult> _writer;
        private readonly CancellationToken _ct;

        public ManualResetEvent Finished { get; } = new(false);

        public ExecutionSink(ChannelWriter<TestResult> writer, CancellationToken ct)
        {
            _writer = writer;
            _ct = ct;
        }

        public bool OnMessage(IMessageSinkMessage message)
        {
            if (_ct.IsCancellationRequested)
            {
                Finished.Set();
                return false;
            }

            switch (message)
            {
                case ITestStarting starting:
                    var startFqn = $"{starting.TestClass.Class.Name}.{starting.TestMethod.Method.Name}";
                    _writer.TryWrite(new TestStarted(startFqn, starting.Test.DisplayName));
                    break;

                case ITestPassed passed:
                    var passFqn = $"{passed.TestClass.Class.Name}.{passed.TestMethod.Method.Name}";
                    _writer.TryWrite(new TestPassed(
                        passFqn,
                        passed.Test.DisplayName,
                        TimeSpan.FromSeconds((double)passed.ExecutionTime)));
                    break;

                case ITestFailed failed:
                    var failFqn = $"{failed.TestClass.Class.Name}.{failed.TestMethod.Method.Name}";
                    _writer.TryWrite(new TestFailed(
                        failFqn,
                        failed.Test.DisplayName,
                        TimeSpan.FromSeconds((double)failed.ExecutionTime),
                        string.Join(Environment.NewLine, failed.Messages),
                        string.Join(Environment.NewLine, failed.StackTraces)));
                    break;

                case ITestSkipped skipped:
                    var skipFqn = $"{skipped.TestClass.Class.Name}.{skipped.TestMethod.Method.Name}";
                    _writer.TryWrite(new TestSkipped(
                        skipFqn,
                        skipped.Test.DisplayName,
                        skipped.Reason));
                    break;

                case ITestOutput output:
                    var outFqn = $"{output.TestClass.Class.Name}.{output.TestMethod.Method.Name}";
                    _writer.TryWrite(new TestOutput(outFqn, output.Output));
                    break;

                case ITestAssemblyFinished:
                    Finished.Set();
                    break;
            }

            return true;
        }
    }

    /// <summary>
    /// Assembly resolver that looks in the test assembly's directory
    /// </summary>
    private class TestAssemblyResolver : IDisposable
    {
        private readonly string _assemblyDir;

        public TestAssemblyResolver(string assemblyDir)
        {
            _assemblyDir = assemblyDir;
            AssemblyLoadContext.Default.Resolving += Resolving;
        }

        private System.Reflection.Assembly? Resolving(AssemblyLoadContext context, System.Reflection.AssemblyName name)
        {
            // Try to find the assembly in the test directory
            var assemblyPath = Path.Combine(_assemblyDir, name.Name + ".dll");
            if (File.Exists(assemblyPath))
            {
                return context.LoadFromAssemblyPath(assemblyPath);
            }
            return null;
        }

        public void Dispose()
        {
            AssemblyLoadContext.Default.Resolving -= Resolving;
        }
    }
}
