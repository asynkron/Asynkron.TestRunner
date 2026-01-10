using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Asynkron.TestRunner;

/// <summary>
/// Live console display for test execution progress
/// </summary>
public class LiveDisplay
{
    private const int PanelWidth = 80;
    private const int ContentWidth = PanelWidth - 4; // Account for panel borders

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly object _lock = new();

    private int _total;
    private int _passed;
    private int _failed;
    private int _skipped;
    private int _hanging;
    private int _crashed;
    private readonly HashSet<string> _running = new();
    private string? _lastCompleted;
    private string? _lastStatus;
    private string? _filter;
    private string? _assemblyName;

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

    public void TestStarted(string displayName)
    {
        lock (_lock) _running.Add(Truncate(displayName, ContentWidth));
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

    public void WorkerRestarted(int remaining)
    {
        lock (_lock)
        {
            _running.Clear();
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

            var layout = new Rows(
                grid,
                new Text(""),
                CreateProgressBar(completed, _total),
                new Text(""),
                CreateRunningSection()
            );

            // Build header: show filter if set, otherwise assembly name
            var headerText = !string.IsNullOrEmpty(_filter)
                ? $"[blue]filter[/] [green]\"{_filter}\"[/]"
                : _assemblyName ?? "Test Progress";

            var panel = new Panel(layout)
                .Header($"{headerText} [dim]({completed}/{_total})[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Expand();

            // Wrap in a fixed-width container
            var table = new Table().NoBorder().HideHeaders();
            table.AddColumn(new TableColumn("").Width(PanelWidth));
            table.AddRow(panel);

            return table;
        }
    }

    private IRenderable CreateProgressBar(int completed, int total)
    {
        if (total == 0) return new Text("");

        var percentage = Math.Min(1.0, (double)completed / total);
        var barWidth = ContentWidth - 7; // Leave room for " 100 %"
        var filled = (int)(percentage * barWidth);
        var empty = barWidth - filled;

        var color = _failed > 0 || _crashed > 0 || _hanging > 0 ? "red" : "green";
        var bar = $"[{color}]{new string('█', filled)}[/][dim]{new string('░', empty)}[/]";

        return new Markup($"{bar} [dim]{percentage:P0}[/]");
    }

    private IRenderable CreateRunningSection()
    {
        var lines = new List<IRenderable>();
        var textWidth = ContentWidth - 4; // Leave room for status icon and spacing
        const int minLines = 5; // Minimum lines to prevent panel shrinking

        if (_lastCompleted != null && _lastStatus != null)
        {
            lines.Add(new Markup($"{_lastStatus} {Markup.Escape(Truncate(_lastCompleted, textWidth))}"));
        }

        if (_running.Count > 0)
        {
            var runningList = _running.Take(3).ToList();
            foreach (var test in runningList)
            {
                lines.Add(new Markup($"[dim]► {Markup.Escape(Truncate(test, textWidth))}[/]"));
            }
            if (_running.Count > 3)
            {
                lines.Add(new Markup($"[dim]  ...and {_running.Count - 3} more running[/]"));
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(new Markup("[dim]Waiting for tests...[/]"));
        }

        // Pad to minimum height to prevent flickering
        while (lines.Count < minLines)
        {
            lines.Add(new Text(""));
        }

        return new Rows(lines);
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
}
