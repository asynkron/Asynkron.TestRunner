using Spectre.Console;

namespace Asynkron.TestRunner;

/// <summary>
/// Result of an isolation run, containing isolated hanging tests and failed batches.
/// </summary>
public record IsolationResult(
    List<string> IsolatedHangingTests,
    List<string> FailedBatches,
    int TotalBatches,
    int PassedBatches,
    TimeSpan TotalDuration);

public class IsolateRunner
{
    private const int MaxTestsPerBatch = 5000;

    private readonly TimeoutStrategy _timeoutStrategy;
    private readonly string[] _baseTestArgs;
    private readonly string? _initialFilter;
    private readonly int _maxParallelBatches;

    public IsolateRunner(string[] baseTestArgs, int timeoutSeconds = 30, string? initialFilter = null, int maxParallelBatches = 1)
        : this(baseTestArgs, new TimeoutStrategy(TimeoutMode.Fixed, timeoutSeconds), initialFilter, maxParallelBatches)
    {
    }

    public IsolateRunner(string[] baseTestArgs, TimeoutStrategy timeoutStrategy, string? initialFilter = null, int maxParallelBatches = 1)
    {
        _baseTestArgs = baseTestArgs;
        _timeoutStrategy = timeoutStrategy;
        _initialFilter = initialFilter;
        _maxParallelBatches = maxParallelBatches > 0 ? maxParallelBatches : 1;
    }

    /// <summary>
    /// Gets the maximum number of batches that can run in parallel.
    /// </summary>
    public int MaxParallelBatches => _maxParallelBatches;

    /// <summary>
    /// Gets the current timeout strategy.
    /// </summary>
    public TimeoutStrategy TimeoutStrategy => _timeoutStrategy;

    public Task<int> RunAsync(string? initialFilter = null)
    {
        // TODO: Implement native test isolation via worker process
        throw new NotImplementedException("Native test runner not yet implemented - see GitHub issue #6");
    }

    /// <summary>
    /// Runs isolation and returns detailed results.
    /// </summary>
    public Task<IsolationResult> RunWithResultAsync(string? initialFilter = null)
    {
        // TODO: Implement native test isolation via worker process
        throw new NotImplementedException("Native test runner not yet implemented - see GitHub issue #6");
    }

    #region Test Batching Logic (reusable for native runner)

    private record TestBatch(string Label, List<string> Tests, List<string> FilterPrefixes);

    /// <summary>
    /// Builds test batches from a test tree, grouping tests into batches of MaxTestsPerBatch.
    /// This logic will be reused by the native runner.
    /// </summary>
    internal static List<(string Label, List<string> Tests)> BuildBatchesFromTree(TestTreeNode root, int maxTestsPerBatch = 5000)
    {
        var eligibleNodes = new List<TestTreeNode>();
        CollectEligibleNodes(root, parentOverLimit: true, eligibleNodes, maxTestsPerBatch);

        if (eligibleNodes.Count == 0)
        {
            eligibleNodes.AddRange(GetLeaves(root).OrderBy(n => n.TotalTestCount));
        }

        var batches = new List<(string Label, List<string> Tests)>();
        var currentNodes = new List<TestTreeNode>();
        var currentTests = new List<string>();
        var currentCount = 0;

        foreach (var node in eligibleNodes)
        {
            var nodeTests = TestTree.GetAllTests(node);
            if (currentCount + nodeTests.Count > maxTestsPerBatch && currentNodes.Count > 0)
            {
                batches.Add(BuildCombinedBatch(currentNodes, currentTests));
                currentNodes.Clear();
                currentTests.Clear();
                currentCount = 0;
            }

            currentNodes.Add(node);
            currentTests.AddRange(nodeTests);
            currentCount += nodeTests.Count;
        }

        if (currentNodes.Count > 0)
        {
            batches.Add(BuildCombinedBatch(currentNodes, currentTests));
        }

        return batches;
    }

    private static void CollectEligibleNodes(TestTreeNode node, bool parentOverLimit, List<TestTreeNode> eligible, int maxTestsPerBatch)
    {
        var overLimit = node.TotalTestCount > maxTestsPerBatch;

        if (!overLimit && parentOverLimit)
        {
            eligible.Add(node);
            return;
        }

        foreach (var child in node.Children.OrderBy(c => c.Name))
        {
            CollectEligibleNodes(child, overLimit, eligible, maxTestsPerBatch);
        }
    }

    private static IEnumerable<TestTreeNode> GetLeaves(TestTreeNode node)
    {
        if (node.Children.Count == 0)
            yield return node;
        else
        {
            foreach (var child in node.Children)
            {
                foreach (var leaf in GetLeaves(child))
                    yield return leaf;
            }
        }
    }

    private static (string Label, List<string> Tests) BuildCombinedBatch(List<TestTreeNode> nodes, List<string> tests)
    {
        var label = nodes.Count == 1
            ? GetNodeLabel(nodes[0])
            : $"{nodes.Count} branches ({tests.Count} tests)";

        return (label, new List<string>(tests));
    }

    private static string GetNodeLabel(TestTreeNode node)
    {
        return string.IsNullOrWhiteSpace(node.FullPath)
            ? "All Tests"
            : node.FullPath;
    }

    #endregion
}
