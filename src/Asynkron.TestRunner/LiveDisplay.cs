using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Asynkron.TestRunner;

/// <summary>
/// Live console display for test execution progress
/// </summary>
public class LiveDisplay
{
    private static int PanelWidth => Math.Max(80, Console.WindowWidth - 2);
    private static int PanelHeight => Math.Max(20, Console.WindowHeight - 2);
    private static int ContentWidth => PanelWidth - 4; // Account for panel borders
    private static int ContentHeight => PanelHeight - 6; // Account for panel borders, header, stats, progress bar

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _lock = new();

    private int _total;
    private int _passed;
    private int _failed;
    private int _skipped;
    private int _hanging;
    private int _crashed;
    private readonly Dictionary<string, DateTime> _running = new();
    private readonly HashSet<string> _stuckTests = new();
    private readonly List<SlotStatus> _completionOrder = new(); // Actual completion order for heat map
    private string? _lastCompleted;
    private string? _lastStatus;
    private string? _filter;
    private string? _assemblyName;
    private int _workerCount = 1;
    private int _timeoutSeconds = 30;
    private int _pendingCount;
    private int _suspiciousCount;
    private int _confirmedCount;
    private int _currentBatchSize;
    private readonly Dictionary<int, WorkerState> _workerStates = new();

    private enum SlotStatus { Pending, Passed, Failed, Hanging, Crashed }

    private class WorkerState
    {
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public bool IsRestarting { get; set; }
        public bool IsComplete { get; set; }
        public int BatchOffset { get; set; }
        public int BatchSize { get; set; }
        public List<SlotStatus> CompletedResults { get; } = new();
    }

    public void SetTotal(int total)
    {
        lock (_lock) _total = total;
    }

    public void SetFilter(string? filter)
    {
        lock (_lock) _filter = filter;
    }

    public void SetAssembly(string assemblyPath)
    {
        lock (_lock) _assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
    }

    public void SetWorkerCount(int count)
    {
        lock (_lock)
        {
            _workerCount = count;
            for (var i = 0; i < count; i++)
                _workerStates[i] = new WorkerState();
        }
    }

    public void SetTimeout(int seconds)
    {
        lock (_lock) _timeoutSeconds = seconds;
    }

    public void SetQueueStats(int pendingCount, int suspiciousCount, int confirmedCount, int currentBatchSize)
    {
        lock (_lock)
        {
            _pendingCount = pendingCount;
            _suspiciousCount = suspiciousCount;
            _confirmedCount = confirmedCount;
            _currentBatchSize = currentBatchSize;
        }
    }

    public void SetWorkerBatch(int workerIndex, int offset, int size)
    {
        lock (_lock)
        {
            if (_workerStates.TryGetValue(workerIndex, out var state))
            {
                state.BatchOffset = offset;
                state.BatchSize = size;
            }
        }
    }

    public void WorkerTestPassed(int workerIndex)
    {
        lock (_lock)
        {
            if (_workerStates.TryGetValue(workerIndex, out var state))
                state.CompletedResults.Add(SlotStatus.Passed);
        }
    }

    public void WorkerTestFailed(int workerIndex)
    {
        lock (_lock)
        {
            if (_workerStates.TryGetValue(workerIndex, out var state))
                state.CompletedResults.Add(SlotStatus.Failed);
        }
    }

    public void WorkerTestHanging(int workerIndex)
    {
        lock (_lock)
        {
            if (_workerStates.TryGetValue(workerIndex, out var state))
                state.CompletedResults.Add(SlotStatus.Hanging);
        }
    }

    public void WorkerTestCrashed(int workerIndex)
    {
        lock (_lock)
        {
            if (_workerStates.TryGetValue(workerIndex, out var state))
                state.CompletedResults.Add(SlotStatus.Crashed);
        }
    }

    public void WorkerActivity(int workerIndex)
    {
        lock (_lock)
        {
            if (_workerStates.TryGetValue(workerIndex, out var state))
            {
                state.LastActivity = DateTime.UtcNow;
                state.IsRestarting = false;
            }
        }
    }

    public void WorkerRestarting(int workerIndex)
    {
        lock (_lock)
        {
            if (_workerStates.TryGetValue(workerIndex, out var state))
                state.IsRestarting = true;
        }
    }

    public void WorkerComplete(int workerIndex)
    {
        lock (_lock)
        {
            if (_workerStates.TryGetValue(workerIndex, out var state))
                state.IsComplete = true;
        }
    }

    public void TestStarted(string displayName)
    {
        lock (_lock) _running[displayName] = DateTime.UtcNow;
    }

    /// <summary>
    /// Removes a test from the running list without marking any status.
    /// Used when moving tests to isolated retry.
    /// </summary>
    public void TestRemoved(string displayName)
    {
        lock (_lock) _running.Remove(displayName);
    }

    public void TestPassed(string displayName)
    {
        lock (_lock)
        {
            _passed++;
            _completionOrder.Add(SlotStatus.Passed);
            _running.Remove(displayName);
            _lastCompleted = displayName;
            _lastStatus = "[green]✓[/]";
        }
    }

    public void TestFailed(string displayName)
    {
        lock (_lock)
        {
            _failed++;
            _completionOrder.Add(SlotStatus.Failed);
            _running.Remove(displayName);
            _lastCompleted = displayName;
            _lastStatus = "[red]✗[/]";
        }
    }

    public void TestSkipped(string displayName)
    {
        lock (_lock)
        {
            _skipped++;
            _completionOrder.Add(SlotStatus.Passed); // Skipped = green in heat map
            _running.Remove(displayName);
            _lastCompleted = displayName;
            _lastStatus = "[yellow]○[/]";
        }
    }

    public void TestHanging(string displayName)
    {
        lock (_lock)
        {
            _hanging++;
            _completionOrder.Add(SlotStatus.Hanging);
            _running.Remove(displayName);
            _lastCompleted = displayName;
            _lastStatus = "[red]⏱[/]";
        }
    }

    public void TestCrashed(string displayName)
    {
        lock (_lock)
        {
            _crashed++;
            _completionOrder.Add(SlotStatus.Crashed);
            _running.Remove(displayName);
            _lastCompleted = displayName;
            _lastStatus = "[red]💥[/]";
        }
    }


    public IRenderable Render()
    {
        lock (_lock)
        {
            var completed = _passed + _failed + _skipped + _hanging + _crashed;
            var elapsed = _stopwatch.Elapsed;
            var rate = elapsed.TotalSeconds > 0 ? completed / elapsed.TotalSeconds : 0;

            var grid = new Grid();
            grid.AddColumn(new GridColumn().NoWrap());
            grid.AddColumn(new GridColumn().NoWrap());
            grid.AddColumn(new GridColumn().NoWrap());
            grid.AddColumn(new GridColumn().NoWrap());
            grid.AddColumn(new GridColumn().NoWrap());
            grid.AddColumn(new GridColumn().NoWrap());

            // Stats row
            grid.AddRow(
                new Markup($"[green]{_passed}[/] passed"),
                new Markup($"[red]{_failed}[/] failed"),
                new Markup($"[yellow]{_skipped}[/] skipped"),
                new Markup($"[red]{_hanging}[/] hanging"),
                new Markup($"[red]{_crashed}[/] crashed"),
                new Markup($"[dim]{elapsed:mm\\:ss}[/] [dim]({rate:F1}/s)[/]")
            );

            var layoutItems = new List<IRenderable>
            {
                grid,
                new Text(""),
                CreateProgressBar(completed, _total),
                new Text(""),
                CreateRunningSection()
            };

            // Add worker status bar if multiple workers
            if (_workerCount > 1)
            {
                layoutItems.Add(new Text(""));
                layoutItems.Add(CreateWorkerStatusBar());
            }

            var layout = new Rows(layoutItems);

            // Build header: show filter if set, otherwise assembly name
            var headerText = !string.IsNullOrEmpty(_filter)
                ? $"[blue]filter[/] [green]\"{_filter}\"[/]"
                : _assemblyName ?? "Test Progress";

            var workerText = _workerCount > 1 ? "" : ""; // Removed from header, shown in bar now

            var panel = new Panel(layout)
                .Header($"{headerText}{workerText} [blue]({completed}/{_total})[/]")
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

    private IRenderable CreateProgressBar(int completed, int total)
    {
        if (total == 0) return new Text("");

        var barWidth = ContentWidth - 7; // Leave room for " 100 %"
        var percentage = Math.Min(1.0, (double)completed / total);

        // Use actual completion order, pad with pending for remaining tests
        var allResults = new List<SlotStatus>(_completionOrder);
        while (allResults.Count < total)
            allResults.Add(SlotStatus.Pending);

        // Use gradient heat map with half-block characters (2 pixels per char)
        var bar = RenderGradientHeatMap(allResults, total, barWidth);

        return new Markup($"{bar} [dim]{percentage:P0}[/]");
    }

    private static string RenderGradientHeatMap(List<SlotStatus> results, int total, int barWidth)
    {
        var bar = new System.Text.StringBuilder();
        var pixelCount = barWidth * 2; // 2 horizontal pixels per character

        for (var i = 0; i < barWidth; i++)
        {
            // Calculate colors for left and right pixels
            var leftPixelIdx = i * 2;
            var rightPixelIdx = i * 2 + 1;

            var leftColor = CalculatePixelColor(results, total, leftPixelIdx, pixelCount);
            var rightColor = CalculatePixelColor(results, total, rightPixelIdx, pixelCount);

            // Use right half block: ▐
            // Background = left color, Foreground = right color
            bar.Append($"[rgb({rightColor.R},{rightColor.G},{rightColor.B}) on rgb({leftColor.R},{leftColor.G},{leftColor.B})]▐[/]");
        }

        return bar.ToString();
    }

    private static (int R, int G, int B) CalculatePixelColor(List<SlotStatus> results, int total, int pixelIdx, int pixelCount)
    {
        // Map pixel to range of tests
        var startIdx = (int)((double)pixelIdx / pixelCount * total);
        var endIdx = (int)((double)(pixelIdx + 1) / pixelCount * total);
        if (endIdx <= startIdx) endIdx = startIdx + 1;

        // Count statuses in this range
        int passed = 0, failed = 0, hanging = 0, crashed = 0, pending = 0;
        for (var j = startIdx; j < endIdx && j < results.Count; j++)
        {
            switch (results[j])
            {
                case SlotStatus.Passed: passed++; break;
                case SlotStatus.Failed: failed++; break;
                case SlotStatus.Hanging: hanging++; break;
                case SlotStatus.Crashed: crashed++; break;
                default: pending++; break;
            }
        }
        // Count any indices beyond results as pending
        pending += Math.Max(0, endIdx - results.Count);

        var rangeSize = endIdx - startIdx;
        if (rangeSize == 0) return (40, 40, 40); // dim grey for empty

        // If all pending, return dim
        if (pending == rangeSize) return (60, 60, 60);

        // Calculate ratios (excluding pending from the denominator)
        var completed = passed + failed + hanging + crashed;
        if (completed == 0) return (60, 60, 60);

        var passRatio = (double)passed / completed;
        var failRatio = (double)failed / completed;
        var crashRatio = (double)crashed / completed;
        var hangRatio = (double)hanging / completed;

        // Base color: interpolate green (pass) to red (fail)
        // Green: (0, 200, 0), Red: (200, 0, 0)
        var r = (int)(failRatio * 220 + crashRatio * 180);
        var g = (int)(passRatio * 220);
        var b = (int)(crashRatio * 200 + hangRatio * 150); // Blue tint for crashes/hangs

        // Clamp values
        r = Math.Clamp(r, 0, 255);
        g = Math.Clamp(g, 0, 255);
        b = Math.Clamp(b, 0, 255);

        // Boost minimum brightness if we have results
        if (r < 40 && g < 40 && b < 40)
        {
            r = g = b = 60;
        }

        return (r, g, b);
    }

    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private IRenderable CreateRunningSection()
    {
        var lines = new List<IRenderable>();
        var textWidth = ContentWidth - 4; // Leave room for status icon and spacing

        // Dynamic sizing based on terminal height
        var availableLines = ContentHeight - (_workerCount > 1 ? 2 : 0); // Reserve space for worker bar if needed
        var maxRunningLines = Math.Max(8, availableLines - 2); // Leave room for last completed + overflow message
        var minLines = availableLines; // Fill entire available space

        // Show last completed test
        if (_lastCompleted != null && _lastStatus != null)
        {
            lines.Add(new Markup($"{_lastStatus} {Markup.Escape(Truncate(_lastCompleted, textWidth))}"));
        }

        if (_running.Count > 0)
        {
            // Sort by start time (oldest first) so stuck tests bubble to top
            var runningList = _running.OrderBy(x => x.Value).Take(maxRunningLines).ToList();
            var baseFrame = (int)(_stopwatch.ElapsedMilliseconds / 80); // Cycle every 80ms
            var now = DateTime.UtcNow;

            for (var i = 0; i < runningList.Count; i++)
            {
                var (test, startTime) = runningList[i];
                var age = (now - startTime).TotalSeconds;
                var spinner = SpinnerFrames[(baseFrame + i) % SpinnerFrames.Length];

                // Smooth color fade from dim gray to red based on timeout progress
                var ratio = Math.Clamp(age / _timeoutSeconds, 0, 1);
                var (r, g, b) = Mix((128, 128, 128), (255, 60, 60), ratio);
                var color = $"rgb({r},{g},{b})";

                var ageStr = age >= 1 ? $" [{color}]{age:F0}s[/]" : "";
                lines.Add(new Markup($"[cyan]{spinner}[/] [{color}]{Markup.Escape(Truncate(test, textWidth - 5))}{ageStr}[/]"));
            }

            if (_running.Count > maxRunningLines)
            {
                lines.Add(new Markup($"[dim]  ...and {_running.Count - maxRunningLines} more running[/]"));
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(new Markup("[dim]Waiting for tests...[/]"));
        }

        // Pad to fill available height to prevent flickering
        while (lines.Count < minLines)
        {
            lines.Add(new Text(""));
        }

        return new Rows(lines);
    }

    private IRenderable CreateWorkerStatusBar()
    {
        var parts = new List<string> { "[dim]workers:[/]" };

        for (var i = 0; i < _workerCount; i++)
        {
            if (!_workerStates.TryGetValue(i, out var state))
            {
                parts.Add("[dim]○[/]");
                continue;
            }

            if (state.IsComplete)
            {
                parts.Add("[green]✓[/]");
                continue;
            }

            if (state.IsRestarting)
            {
                parts.Add("[yellow]⏳[/]");
                continue;
            }

            // Calculate health based on time since last activity
            var elapsed = (DateTime.UtcNow - state.LastActivity).TotalSeconds;
            var ratio = Math.Min(1.0, elapsed / _timeoutSeconds);

            // Color gradient: green → yellow → orange → red
            var color = ratio switch
            {
                < 0.25 => "green",
                < 0.5 => "yellow",
                < 0.75 => "orange3",
                _ => "red"
            };

            parts.Add($"[{color}]●[/]");
        }

        // Show queue stats
        parts.Add($"[dim]|[/] [blue]{_pendingCount}[/] [dim]pending[/]");
        if (_suspiciousCount > 0)
            parts.Add($"[dim]|[/] [yellow]{_suspiciousCount}[/] [dim]suspect[/]");
        if (_confirmedCount > 0)
            parts.Add($"[dim]|[/] [red]{_confirmedCount}[/] [dim]confirmed[/]");
        parts.Add($"[dim]| batch={_currentBatchSize}[/]");

        return new Markup(string.Join(" ", parts));
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Mix two colors. t=0 returns color1, t=1 returns color2, t=0.5 returns 50/50 mix.
    /// </summary>
    private static (int R, int G, int B) Mix((int R, int G, int B) color1, (int R, int G, int B) color2, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (
            (int)(color1.R + (color2.R - color1.R) * t),
            (int)(color1.G + (color2.G - color1.G) * t),
            (int)(color1.B + (color2.B - color1.B) * t)
        );
    }

    public (int passed, int failed, int skipped, int hanging, int crashed) GetCounts()
    {
        lock (_lock)
        {
            return (_passed, _failed, _skipped, _hanging, _crashed);
        }
    }

    /// <summary>
    /// Gets tests that have been running longer than the timeout.
    /// These should be considered stuck and killed.
    /// </summary>
    public List<string> GetStuckTests()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var stuck = new List<string>();

            foreach (var (test, startTime) in _running)
            {
                if ((now - startTime).TotalSeconds >= _timeoutSeconds)
                {
                    stuck.Add(test);
                    _stuckTests.Add(test);
                }
            }

            return stuck;
        }
    }

    /// <summary>
    /// Checks if a test was previously identified as stuck
    /// </summary>
    public bool WasStuck(string displayName)
    {
        lock (_lock)
        {
            return _stuckTests.Contains(displayName);
        }
    }
}
