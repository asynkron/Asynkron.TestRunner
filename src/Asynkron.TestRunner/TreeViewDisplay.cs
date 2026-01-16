using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Asynkron.TestRunner;

/// <summary>
/// Settings for tree view display
/// </summary>
public class TreeViewSettings
{
    /// <summary>
    /// Maximum depth to display (0 = namespace only, 1 = namespace+class, 2 = namespace+class+method)
    /// </summary>
    public int MaxDepth { get; set; } = 1; // Default: namespace + class

    /// <summary>
    /// Number of visible rows in the scrollable area
    /// </summary>
    public int VisibleRows { get; set; } = 20;

    public static TreeViewSettings Default => new();
}

/// <summary>
/// Represents a node in the test tree with aggregated status
/// </summary>
public class TreeViewNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public int Depth { get; set; }
    public List<TreeViewNode> Children { get; } = [];
    public List<string> Tests { get; } = []; // Leaf test FQNs at this node

    // Counts
    public int TotalTests { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int Hanging { get; set; }
    public int Crashed { get; set; }

    public int Completed => Passed + Failed + Skipped + Hanging + Crashed;
    public bool IsComplete => Completed >= TotalTests && TotalTests > 0;
    public bool HasFailures => Failed > 0 || Hanging > 0 || Crashed > 0;

    /// <summary>
    /// Gets the status color for this node
    /// </summary>
    public string GetStatusColor()
    {
        if (!IsComplete)
        {
            return "blue"; // In progress
        }

        return HasFailures ? "red" : "green";
    }

    /// <summary>
    /// Gets display string with counts
    /// </summary>
    public string GetDisplayText()
    {
        if (TotalTests == 0)
        {
            return Name;
        }

        return $"{Name} ({Completed}/{TotalTests})";
    }
}

/// <summary>
/// Alternative TUI display showing tests as a scrollable tree
/// </summary>
public class TreeViewDisplay
{
    // Dynamic sizing based on terminal
    private static int PanelWidth => Math.Max(80, Console.WindowWidth - 2);
    private static int PanelHeight => Math.Max(20, Console.WindowHeight - 2);
    private static int ContentWidth => PanelWidth - 4; // Account for panel borders
    private static int ContentHeight => PanelHeight - 8; // Account for panel borders, header, stats, progress bar, scroll indicator

    private readonly object _lock = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TreeViewSettings _settings;

    // Tree structure
    private readonly TreeViewNode _root = new() { Name = "Tests", FullPath = "" };
    private readonly Dictionary<string, TreeViewNode> _nodesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeViewNode> _testToNode = new(StringComparer.OrdinalIgnoreCase);

    // Flattened visible nodes for scrolling
    private List<TreeViewNode> _flattenedNodes = [];
    private int _scrollOffset;

    // Global stats
    private int _totalTests;
    private int _totalPassed;
    private int _totalFailed;
    private int _totalSkipped;
    private int _totalHanging;
    private int _totalCrashed;

    // Display state
    private string? _filter;
    private string? _assemblyName;
    private int _workerCount = 1;

    public TreeViewDisplay(TreeViewSettings? settings = null)
    {
        _settings = settings ?? TreeViewSettings.Default;
    }

    /// <summary>
    /// Initialize the tree with discovered tests
    /// </summary>
    public void Initialize(IEnumerable<string> testFqns)
    {
        lock (_lock)
        {
            foreach (var fqn in testFqns)
            {
                AddTest(fqn);
            }

            // Calculate totals
            CalculateTotals(_root);
            _totalTests = _root.TotalTests;

            // Flatten for display
            RebuildFlattenedList();
        }
    }

    private void AddTest(string fqn)
    {
        // Parse FQN: Namespace.SubNs.Class.Method or Namespace.SubNs.Class.Method(args)
        var baseFqn = GetBaseFqn(fqn);
        var parts = baseFqn.Split('.');

        if (parts.Length < 2)
        {
            return; // Invalid FQN
        }

        var current = _root;
        var pathSoFar = "";

        // Build path up to MaxDepth
        // Depth 0 = first part (usually top-level namespace)
        // Depth 1 = second part (sub-namespace or class)
        // etc.
        var maxParts = Math.Min(parts.Length, _settings.MaxDepth + 2); // +2 because we want namespace.class minimum

        for (var i = 0; i < maxParts; i++)
        {
            var part = parts[i];
            pathSoFar = string.IsNullOrEmpty(pathSoFar) ? part : $"{pathSoFar}.{part}";

            if (!_nodesByPath.TryGetValue(pathSoFar, out var child))
            {
                child = new TreeViewNode
                {
                    Name = part,
                    FullPath = pathSoFar,
                    Depth = i
                };
                current.Children.Add(child);
                _nodesByPath[pathSoFar] = child;
            }

            current = child;
        }

        // Add test to the leaf node
        current.Tests.Add(fqn);
        _testToNode[fqn] = current;
    }

    private static string GetBaseFqn(string fqn)
    {
        var parenIndex = fqn.IndexOf('(');
        return parenIndex > 0 ? fqn[..parenIndex] : fqn;
    }

    private static int CalculateTotals(TreeViewNode node)
    {
        var count = node.Tests.Count;
        foreach (var child in node.Children)
        {
            count += CalculateTotals(child);
        }

        node.TotalTests = count;
        return count;
    }

    private void RebuildFlattenedList()
    {
        _flattenedNodes = [];
        FlattenNode(_root, 0);
    }

    private void FlattenNode(TreeViewNode node, int depth)
    {
        if (node != _root) // Don't include root
        {
            _flattenedNodes.Add(node);
        }

        foreach (var child in node.Children.OrderBy(c => c.Name))
        {
            FlattenNode(child, depth + 1);
        }
    }

    public void SetFilter(string? filter)
    {
        lock (_lock)
        {
            _filter = filter;
        }
    }

    public void SetAssembly(string assemblyPath)
    {
        lock (_lock)
        {
            _assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        }
    }

    public void SetWorkerCount(int count)
    {
        lock (_lock)
        {
            _workerCount = count;
        }
    }

    public void SetTotal(int total)
    {
        // Total is set via Initialize
    }

    public void SetTimeout(int seconds)
    {
        // Not used in tree view
    }

    public void SetQueueStats(int pendingCount, int suspiciousCount, int confirmedCount, int currentBatchSize)
    {
        // Could display queue stats if needed
    }

    public void SetWorkerBatch(int workerIndex, int offset, int size)
    {
        // Not used in tree view
    }

    public void WorkerTestPassed(int workerIndex) { }
    public void WorkerTestFailed(int workerIndex) { }
    public void WorkerTestHanging(int workerIndex) { }
    public void WorkerTestCrashed(int workerIndex) { }
    public void WorkerActivity(int workerIndex) { }
    public void WorkerRestarting(int workerIndex) { }
    public void WorkerComplete(int workerIndex) { }

    public void TestStarted(string displayName)
    {
        // Could highlight currently running tests
    }

    public void TestRemoved(string displayName)
    {
        // Test moved to retry queue
    }

    public void TestPassed(string fqn)
    {
        lock (_lock)
        {
            _totalPassed++;
            if (_testToNode.TryGetValue(fqn, out var node))
            {
                node.Passed++;
                PropagateUpdate(node);
            }
        }
    }

    public void TestFailed(string fqn)
    {
        lock (_lock)
        {
            _totalFailed++;
            if (_testToNode.TryGetValue(fqn, out var node))
            {
                node.Failed++;
                PropagateUpdate(node);
            }
        }
    }

    public void TestSkipped(string fqn)
    {
        lock (_lock)
        {
            _totalSkipped++;
            if (_testToNode.TryGetValue(fqn, out var node))
            {
                node.Skipped++;
                PropagateUpdate(node);
            }
        }
    }

    public void TestHanging(string fqn)
    {
        lock (_lock)
        {
            _totalHanging++;
            if (_testToNode.TryGetValue(fqn, out var node))
            {
                node.Hanging++;
                PropagateUpdate(node);
            }
        }
    }

    public void TestCrashed(string fqn)
    {
        lock (_lock)
        {
            _totalCrashed++;
            if (_testToNode.TryGetValue(fqn, out var node))
            {
                node.Crashed++;
                PropagateUpdate(node);
            }
        }
    }

    private static void PropagateUpdate(TreeViewNode node)
    {
        // Updates are already at the leaf level, parent totals recalculated during render
        _ = node; // Suppress unused parameter warning
    }

    /// <summary>
    /// Scroll up by one row
    /// </summary>
    public void ScrollUp()
    {
        lock (_lock)
        {
            _scrollOffset = Math.Max(0, _scrollOffset - 1);
        }
    }

    /// <summary>
    /// Scroll down by one row
    /// </summary>
    public void ScrollDown()
    {
        lock (_lock)
        {
            var visibleRows = GetVisibleRows();
            var maxOffset = Math.Max(0, _flattenedNodes.Count - visibleRows);
            _scrollOffset = Math.Min(maxOffset, _scrollOffset + 1);
        }
    }

    /// <summary>
    /// Scroll up by a page
    /// </summary>
    public void PageUp()
    {
        lock (_lock)
        {
            var visibleRows = GetVisibleRows();
            _scrollOffset = Math.Max(0, _scrollOffset - visibleRows);
        }
    }

    /// <summary>
    /// Scroll down by a page
    /// </summary>
    public void PageDown()
    {
        lock (_lock)
        {
            var visibleRows = GetVisibleRows();
            var maxOffset = Math.Max(0, _flattenedNodes.Count - visibleRows);
            _scrollOffset = Math.Min(maxOffset, _scrollOffset + visibleRows);
        }
    }

    public IRenderable Render()
    {
        lock (_lock)
        {
            var completed = _totalPassed + _totalFailed + _totalSkipped + _totalHanging + _totalCrashed;
            var elapsed = _stopwatch.Elapsed;
            var rate = elapsed.TotalSeconds > 0 ? completed / elapsed.TotalSeconds : 0;

            // Recalculate parent node aggregates
            RecalculateAggregates(_root);

            // Dynamic visible rows based on terminal height
            var visibleRows = Math.Max(5, ContentHeight - 2); // Reserve for scroll indicator

            // Build the display
            var rows = new List<IRenderable>();

            // Stats row
            var statsGrid = new Grid();
            statsGrid.AddColumn(new GridColumn().NoWrap());
            statsGrid.AddColumn(new GridColumn().NoWrap());
            statsGrid.AddColumn(new GridColumn().NoWrap());
            statsGrid.AddColumn(new GridColumn().NoWrap());
            statsGrid.AddColumn(new GridColumn().NoWrap());
            statsGrid.AddColumn(new GridColumn().NoWrap());

            statsGrid.AddRow(
                new Markup($"[green]{_totalPassed}[/] passed"),
                new Markup($"[red]{_totalFailed}[/] failed"),
                new Markup($"[yellow]{_totalSkipped}[/] skipped"),
                new Markup($"[red]{_totalHanging}[/] hanging"),
                new Markup($"[red]{_totalCrashed}[/] crashed"),
                new Markup($"[dim]{elapsed:mm\\:ss}[/] [dim]({rate:F1}/s)[/]")
            );

            rows.Add(statsGrid);
            rows.Add(new Text(""));

            // Progress bar
            var percentage = _totalTests > 0 ? (double)completed / _totalTests : 0;
            var barWidth = ContentWidth - 7; // Leave room for percentage
            var filledWidth = (int)(barWidth * percentage);
            var emptyWidth = barWidth - filledWidth;

            var progressBar = new Markup(
                $"[green]{new string('█', filledWidth)}[/][dim]{new string('░', emptyWidth)}[/] [dim]{percentage:P0}[/]");
            rows.Add(progressBar);
            rows.Add(new Text(""));

            // Tree view with scroll - dynamic row count
            var treeRows = new List<IRenderable>();
            var visibleNodes = _flattenedNodes
                .Skip(_scrollOffset)
                .Take(visibleRows)
                .ToList();

            foreach (var node in visibleNodes)
            {
                var indent = new string(' ', node.Depth * 2);
                var icon = GetNodeIcon(node);
                var color = node.GetStatusColor();
                var text = node.GetDisplayText();

                // Truncate if too long
                var maxTextLen = ContentWidth - (node.Depth * 2) - 4;
                if (text.Length > maxTextLen && maxTextLen > 3)
                {
                    text = text[..(maxTextLen - 3)] + "...";
                }

                treeRows.Add(new Markup($"{indent}{icon} [{color}]{Markup.Escape(text)}[/]"));
            }

            // Pad with empty rows to fill the space
            while (treeRows.Count < visibleRows)
            {
                treeRows.Add(new Text(""));
            }

            rows.Add(new Rows(treeRows));

            // Scroll indicator
            if (_flattenedNodes.Count > visibleRows)
            {
                var scrollInfo = $"[dim]↑↓ scroll ({_scrollOffset + 1}-{Math.Min(_scrollOffset + visibleRows, _flattenedNodes.Count)}/{_flattenedNodes.Count})[/]";
                rows.Add(new Text(""));
                rows.Add(new Markup(scrollInfo));
            }
            else
            {
                rows.Add(new Text(""));
                rows.Add(new Text(""));
            }

            var layout = new Rows(rows);

            // Build header
            var headerText = !string.IsNullOrEmpty(_filter)
                ? $"[blue]filter[/] [green]\"{_filter}\"[/]"
                : _assemblyName ?? "Test Tree";

            var workerText = _workerCount > 1 ? $" [dim]({_workerCount} workers)[/]" : "";

            var panel = new Panel(layout)
                .Header($"{headerText}{workerText} [blue]({completed}/{_totalTests})[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Expand();

            // Wrap in a fixed-size container (full terminal width and height)
            var table = new Table().NoBorder().HideHeaders();
            table.AddColumn(new TableColumn("").Width(PanelWidth));
            table.AddRow(panel);

            // Add padding rows to fill terminal height
            var currentHeight = PanelHeight;
            var paddingNeeded = Console.WindowHeight - currentHeight - 1;
            for (var i = 0; i < paddingNeeded; i++)
            {
                table.AddEmptyRow();
            }

            return table;
        }
    }

    /// <summary>
    /// Gets the number of visible rows based on current terminal size
    /// </summary>
    private static int GetVisibleRows()
    {
        return Math.Max(5, ContentHeight - 2);
    }

    private static string GetNodeIcon(TreeViewNode node)
    {
        if (!node.IsComplete)
        {
            return "○"; // In progress
        }

        if (node.HasFailures)
        {
            return "✗"; // Has failures
        }

        return "✓"; // All passed
    }

    private static void RecalculateAggregates(TreeViewNode node)
    {
        // Reset counts from children
        if (node.Children.Count > 0)
        {
            node.Passed = 0;
            node.Failed = 0;
            node.Skipped = 0;
            node.Hanging = 0;
            node.Crashed = 0;

            foreach (var child in node.Children)
            {
                RecalculateAggregates(child);
                node.Passed += child.Passed;
                node.Failed += child.Failed;
                node.Skipped += child.Skipped;
                node.Hanging += child.Hanging;
                node.Crashed += child.Crashed;
            }
        }
        // Leaf nodes keep their own counts
    }
}
