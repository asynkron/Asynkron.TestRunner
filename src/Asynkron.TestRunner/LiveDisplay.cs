using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Asynkron.TestRunner;

/// <summary>
/// Live console display for test execution progress
/// </summary>
public class LiveDisplay
{
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

    public void SetTotal(int total)
    {
        lock (_lock) _total = total;
    }

    public void TestStarted(string displayName)
    {
        lock (_lock) _running.Add(Truncate(displayName, 60));
    }

    public void TestPassed(string displayName)
    {
        lock (_lock)
        {
            _passed++;
            _running.Remove(Truncate(displayName, 60));
            _lastCompleted = displayName;
            _lastStatus = "[green]✓[/]";
        }
    }

    public void TestFailed(string displayName)
    {
        lock (_lock)
        {
            _failed++;
            _running.Remove(Truncate(displayName, 60));
            _lastCompleted = displayName;
            _lastStatus = "[red]✗[/]";
        }
    }

    public void TestSkipped(string displayName)
    {
        lock (_lock)
        {
            _skipped++;
            _running.Remove(Truncate(displayName, 60));
            _lastCompleted = displayName;
            _lastStatus = "[yellow]○[/]";
        }
    }

    public void TestHanging(string displayName)
    {
        lock (_lock)
        {
            _hanging++;
            _running.Remove(Truncate(displayName, 60));
            _lastCompleted = displayName;
            _lastStatus = "[red]⏱[/]";
        }
    }

    public void TestCrashed(string displayName)
    {
        lock (_lock)
        {
            _crashed++;
            _running.Remove(Truncate(displayName, 60));
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

            return new Panel(layout)
                .Header($"[bold]Test Progress[/] [dim]({completed}/{_total})[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey);
        }
    }

    private IRenderable CreateProgressBar(int completed, int total)
    {
        if (total == 0) return new Text("");

        var percentage = (double)completed / total;
        var width = Math.Min(60, Console.WindowWidth - 10);
        var filled = (int)(percentage * width);
        var empty = width - filled;

        var color = _failed > 0 || _crashed > 0 || _hanging > 0 ? "red" : "green";
        var bar = $"[{color}]{new string('█', filled)}[/][dim]{new string('░', empty)}[/]";

        return new Markup($"{bar} [dim]{percentage:P0}[/]");
    }

    private IRenderable CreateRunningSection()
    {
        var lines = new List<IRenderable>();

        if (_lastCompleted != null && _lastStatus != null)
        {
            lines.Add(new Markup($"{_lastStatus} {Markup.Escape(Truncate(_lastCompleted, 70))}"));
        }

        if (_running.Count > 0)
        {
            var runningList = _running.Take(3).ToList();
            foreach (var test in runningList)
            {
                lines.Add(new Markup($"[dim]► {Markup.Escape(test)}[/]"));
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
