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
    private string? _lastCompleted;
    private string? _lastStatus;
    private string? _filter;
    private string? _assemblyName;
    private int _workerCount = 1;
    private int _timeoutSeconds = 30;
    private readonly Dictionary<int, WorkerState> _workerStates = new();

    private class WorkerState
    {
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public bool IsRestarting { get; set; }
        public bool IsComplete { get; set; }
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
        lock (_lock) _running[Truncate(displayName, ContentWidth)] = DateTime.UtcNow;
    }

    /// <summary>
    /// Removes a test from the running list without marking any status.
    /// Used when moving tests to isolated retry.
    /// </summary>
    public void TestRemoved(string displayName)
    {
        lock (_lock) _running.Remove(Truncate(displayName, ContentWidth));
    }

    public void TestPassed(string displayName)
    {
        lock (_lock)
        {
            _passed++;
            _running.Remove(Truncate(displayName, ContentWidth));
            _lastCompleted = displayName;
            _lastStatus = "[green]✓[/]";
        }
    }

    public void TestFailed(string displayName)
    {
        lock (_lock)
        {
            _failed++;
            _running.Remove(Truncate(displayName, ContentWidth));
            _lastCompleted = displayName;
            _lastStatus = "[red]✗[/]";
        }
    }

    public void TestSkipped(string displayName)
    {
        lock (_lock)
        {
            _skipped++;
            _running.Remove(Truncate(displayName, ContentWidth));
            _lastCompleted = displayName;
            _lastStatus = "[yellow]○[/]";
        }
    }

    public void TestHanging(string displayName)
    {
        lock (_lock)
        {
            _hanging++;
            _running.Remove(Truncate(displayName, ContentWidth));
            _lastCompleted = displayName;
            _lastStatus = "[red]⏱[/]";
        }
    }

    public void TestCrashed(string displayName)
    {
        lock (_lock)
        {
            _crashed++;
            _running.Remove(Truncate(displayName, ContentWidth));
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

        // Simple: show completed (green/red) then pending (grey)
        var filledWidth = (int)(percentage * barWidth);
        var emptyWidth = barWidth - filledWidth;

        // Calculate color based on pass/fail ratio
        var failRatio = completed > 0 ? (double)(_failed + _crashed + _hanging) / completed : 0;
        var color = failRatio switch
        {
            0 => "green",
            < 0.1 => "yellow",
            < 0.3 => "orange3",
            _ => "red"
        };

        var bar = $"[{color}]{new string('█', filledWidth)}[/][dim]{new string('░', emptyWidth)}[/]";

        return new Markup($"{bar} [dim]{percentage:P0}[/]");
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

                // Color based on age relative to timeout
                var ratio = age / _timeoutSeconds;
                var color = ratio switch
                {
                    >= 1.0 => "red",      // Over timeout - definitely stuck
                    >= 0.75 => "orange3", // Approaching timeout
                    >= 0.5 => "yellow",   // Getting old
                    _ => "dim"            // Normal
                };

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

        return new Markup(string.Join(" ", parts));
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
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
            return _stuckTests.Contains(Truncate(displayName, ContentWidth));
        }
    }
}
