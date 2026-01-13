using System.Globalization;
using Asynkron.TestRunner.Models;
using Spectre.Console;

namespace Asynkron.TestRunner;

internal static class HeatMapRenderer
{
    public static string RenderGradientHeatMap(List<SlotStatus> results, int total, int barWidth)
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
            bar.Append(CultureInfo.InvariantCulture, $"[rgb({rightColor.R},{rightColor.G},{rightColor.B}) on rgb({leftColor.R},{leftColor.G},{leftColor.B})]▐[/]");
        }

        return bar.ToString();
    }

    private static (int R, int G, int B) CalculatePixelColor(List<SlotStatus> results, int total, int pixelIdx, int pixelCount)
    {
        // Map pixel to range of tests
        var startIdx = (int)((double)pixelIdx / pixelCount * total);
        var endIdx = (int)((double)(pixelIdx + 1) / pixelCount * total);
        if (endIdx <= startIdx)
        {
            endIdx = startIdx + 1;
        }

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
        if (rangeSize == 0)
        {
            return (40, 40, 40); // dim grey for empty
        }

        // If all pending, return dim
        if (pending == rangeSize)
        {
            return (60, 60, 60);
        }

        // Calculate ratios (excluding pending from the denominator)
        var completed = passed + failed + hanging + crashed;
        if (completed == 0)
        {
            return (60, 60, 60);
        }

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
}

public static class ChartRenderer
{
    private const int BarWidth = 30;

    // Detect if we can use colors
    private static readonly bool UseColor = DetectColorSupport();

    // ANSI color codes (empty strings if no color support)
    private static string Reset => UseColor ? "\x1b[0m" : "";
    private static string Green => UseColor ? "\x1b[32m" : "";
    private static string Red => UseColor ? "\x1b[31m" : "";
    private static string Yellow => UseColor ? "\x1b[33m" : "";
    private static string Magenta => UseColor ? "\x1b[35m" : "";
    private static string Dim => UseColor ? "\x1b[2m" : "";
    private static string Bold => UseColor ? "\x1b[1m" : "";


    private static bool DetectColorSupport()
    {
        // NO_COLOR is a standard env var to disable colors
        if (Environment.GetEnvironmentVariable("NO_COLOR") != null)
        {
            return false;
        }

        // If output is redirected (piped), don't use colors
        if (Console.IsOutputRedirected)
        {
            return false;
        }

        // Check for dumb terminal
        var term = Environment.GetEnvironmentVariable("TERM");
        if (term == "dumb")
        {
            return false;
        }

        // Check CI environments that may not support colors
        if (Environment.GetEnvironmentVariable("CI") != null &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == null) // GitHub Actions supports colors
        {
            return false;
        }

        return true;
    }

    public static void RenderHistory(List<TestRunResult> results, bool showLatestFirst = true)
    {
        if (results.Count == 0)
        {
            Console.WriteLine($"{Dim}No test history found.{Reset}");
            return;
        }

        var ordered = showLatestFirst
            ? results.OrderByDescending(r => r.Timestamp).ToList()
            : results.OrderBy(r => r.Timestamp).ToList();

        Console.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Test History ({results.Count} runs)[/]");

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.BorderStyle(new Style(Color.Grey));
        table.ShowRowSeparators = false;
        table.AddColumn(new TableColumn("Timestamp").NoWrap());
        table.AddColumn(new TableColumn("Results"));
        table.AddColumn(new TableColumn("Pass Rate").RightAligned());
        table.AddColumn(new TableColumn("Failures").RightAligned());

        foreach (var result in ordered)
        {
            var timestamp = result.Timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var total = result.Passed + result.Failed + result.Skipped + result.TimedOutTests.Count;
            var slots = result.CompletionOrder.Count > 0
                ? new List<SlotStatus>(result.CompletionOrder)
                : BuildFallbackSlots(result);
            var heatMap = HeatMapRenderer.RenderGradientHeatMap(slots, Math.Max(total, slots.Count), BarWidth);

            var passRate = result.PassRate.ToString("F1", CultureInfo.CurrentCulture);
            var passText = $"{result.Passed}/{result.Total} ({passRate}%)";

            var failureCount = result.Failed + result.TimedOutTests.Count;
            var failureText = failureCount > 0
                ? $"[red]✗{failureCount}[/]"
                : "[green]✓[/]";

            table.AddRow(
                new Markup($"[dim]{Markup.Escape(timestamp)}[/]"),
                new Markup(heatMap),
                new Markup(Markup.Escape(passText)),
                new Markup(failureText));
        }

        AnsiConsole.Write(table);
        Console.WriteLine();
    }

    public static void RenderSingleResult(TestRunResult result, TestRunResult? previousRun = null)
    {
        Console.WriteLine();

        var hasFailures = result.FailedTests.Count > 0 || result.TimedOutTests.Count > 0;
        var statusColor = hasFailures ? Red : Green;
        var statusText = hasFailures ? "FAILED" : "PASSED";

        Console.WriteLine($"{statusColor}{Bold}{statusText}{Reset}");
        Console.WriteLine(new string('─', 50));

        Console.WriteLine($"  {Green}Passed:{Reset}  {result.Passed}");
        if (result.TimedOutTests.Count > 0)
        {
            Console.WriteLine($"  {Magenta}Timeout:{Reset} {result.TimedOutTests.Count}");
            Console.WriteLine($"  {Red}Failed:{Reset}  {result.FailedTests.Count}");
        }
        else
        {
            Console.WriteLine($"  {Red}Failed:{Reset}  {result.Failed}");
        }
        Console.WriteLine($"  {Yellow}Skipped:{Reset} {result.Skipped}");
        Console.WriteLine($"  {Dim}Total:{Reset}   {result.Total}");
        Console.WriteLine($"  {Dim}Duration:{Reset} {FormatDuration(result.Duration)}");
        Console.WriteLine($"  {Dim}Pass Rate:{Reset} {result.PassRate:F1}%");

        // Show timed out tests
        if (result.TimedOutTests.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{Magenta}{Bold}Timed Out ({result.TimedOutTests.Count}):{Reset}");
            foreach (var test in result.TimedOutTests.Take(10))
            {
                Console.WriteLine($"  {Magenta}⏱{Reset} {test}");
            }
            if (result.TimedOutTests.Count > 10)
            {
                Console.WriteLine($"  {Dim}... and {result.TimedOutTests.Count - 10} more{Reset}");
            }
        }

        // Show regressions if we have a previous run to compare
        if (previousRun != null)
        {
            RenderRegressions(result, previousRun);
        }

        Console.WriteLine();
    }

    public static void RenderFooterSummary(
        int passed,
        int failed,
        int skipped,
        int hanging,
        int crashed,
        TimeSpan elapsed)
    {
        var passColor = passed > 0 ? Green : Dim;
        var failColor = failed > 0 ? Red : Dim;
        var skipColor = skipped > 0 ? Yellow : Dim;
        var hangColor = hanging > 0 ? Red : Dim;
        var crashColor = crashed > 0 ? Red : Dim;

        Console.WriteLine(
            $"{passColor}{passed} passed{Reset}, " +
            $"{failColor}{failed} failed{Reset}, " +
            $"{skipColor}{skipped} skipped{Reset}, " +
            $"{hangColor}{hanging} hanging{Reset}, " +
            $"{crashColor}{crashed} crashed{Reset} " +
            $"{Dim}({elapsed.TotalSeconds:F1}s){Reset}");
    }

    public static void RenderRegressions(TestRunResult current, TestRunResult previous)
    {
        var regressions = current.GetRegressions(previous);
        var fixes = current.GetFixes(previous);

        if (regressions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{Red}{Bold}Regressions ({regressions.Count}):{Reset}");
            foreach (var test in regressions.Take(20))
            {
                Console.WriteLine($"  {Red}✗{Reset} {test}");
            }
            if (regressions.Count > 20)
            {
                Console.WriteLine($"  {Dim}... and {regressions.Count - 20} more{Reset}");
            }
        }

        if (fixes.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{Green}{Bold}Fixed ({fixes.Count}):{Reset}");
            foreach (var test in fixes.Take(20))
            {
                Console.WriteLine($"  {Green}✓{Reset} {test}");
            }
            if (fixes.Count > 20)
            {
                Console.WriteLine($"  {Dim}... and {fixes.Count - 20} more{Reset}");
            }
        }
    }

    public static void RenderFlakyTests(IReadOnlyList<string> flakyTests, int historyCount)
    {
        if (flakyTests.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{Yellow}{Bold}Flaky (last {historyCount} runs) ({flakyTests.Count}):{Reset}");
        foreach (var test in flakyTests.Take(20))
        {
            Console.WriteLine($"  {Yellow}≈{Reset} {test}");
        }
        if (flakyTests.Count > 20)
        {
            Console.WriteLine($"  {Dim}... and {flakyTests.Count - 20} more{Reset}");
        }
    }

    private static List<SlotStatus> BuildFallbackSlots(TestRunResult result)
    {
        var slots = new List<SlotStatus>(result.Total + result.TimedOutTests.Count);
        if (result.Passed + result.Skipped > 0)
        {
            slots.AddRange(Enumerable.Repeat(SlotStatus.Passed, result.Passed + result.Skipped));
        }
        if (result.Failed > 0)
        {
            slots.AddRange(Enumerable.Repeat(SlotStatus.Failed, result.Failed));
        }
        if (result.TimedOutTests.Count > 0)
        {
            slots.AddRange(Enumerable.Repeat(SlotStatus.Hanging, result.TimedOutTests.Count));
        }

        return slots;
    }


    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.TotalMinutes:F1}m";
        }

        return $"{duration.TotalSeconds:F1}s";
    }

    /// <summary>
    /// Writes a test results summary to a file.
    /// </summary>
    public static void ExportSummary(
        string filePath,
        TestRunResult result)
    {
        using var writer = new StreamWriter(filePath);

        writer.WriteLine($"Test Run Summary - {result.Timestamp:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine(new string('=', 60));
        writer.WriteLine();
        writer.WriteLine($"Total:    {result.Total}");
        writer.WriteLine($"Passed:   {result.Passed}");
        writer.WriteLine($"Failed:   {result.Failed}");
        writer.WriteLine($"Skipped:  {result.Skipped}");
        writer.WriteLine($"Duration: {FormatDuration(result.Duration)}");
        writer.WriteLine($"Pass Rate: {result.PassRate:F1}%");

        if (result.TimedOutTests.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"Timed Out Tests ({result.TimedOutTests.Count}):");
            foreach (var test in result.TimedOutTests)
            {
                writer.WriteLine($"  - {test}");
            }
        }

        if (result.FailedTests.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"Failed Tests ({result.FailedTests.Count}):");
            foreach (var test in result.FailedTests)
            {
                writer.WriteLine($"  - {test}");
            }
        }

        Console.WriteLine($"{Dim}Summary exported to: {filePath}{Reset}");
    }
}
