using Asynkron.TestRunner.Models;

namespace Asynkron.TestRunner;

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
    private static string Dim => UseColor ? "\x1b[2m" : "";
    private static string Bold => UseColor ? "\x1b[1m" : "";

    // Bar characters - use distinct chars when no color
    private static char PassedChar => UseColor ? '█' : '=';
    private static char FailedChar => UseColor ? '█' : 'X';
    private static char SkippedChar => UseColor ? '░' : '-';
    private static char EmptyChar => UseColor ? '░' : '.';

    private static bool DetectColorSupport()
    {
        // NO_COLOR is a standard env var to disable colors
        if (Environment.GetEnvironmentVariable("NO_COLOR") != null)
            return false;

        // If output is redirected (piped), don't use colors
        if (Console.IsOutputRedirected)
            return false;

        // Check for dumb terminal
        var term = Environment.GetEnvironmentVariable("TERM");
        if (term == "dumb")
            return false;

        // Check CI environments that may not support colors
        if (Environment.GetEnvironmentVariable("CI") != null &&
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == null) // GitHub Actions supports colors
            return false;

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

        var maxTotal = ordered.Max(r => r.Total);

        Console.WriteLine();
        Console.WriteLine($"{Bold}Test History ({results.Count} runs){Reset}");
        Console.WriteLine(new string('─', 70));

        foreach (var result in ordered)
        {
            RenderBar(result, maxTotal);
        }

        Console.WriteLine();
    }

    public static void RenderSingleResult(TestRunResult result, TestRunResult? previousRun = null)
    {
        Console.WriteLine();

        var statusColor = result.Failed > 0 ? Red : Green;
        var statusText = result.Failed > 0 ? "FAILED" : "PASSED";

        Console.WriteLine($"{statusColor}{Bold}{statusText}{Reset}");
        Console.WriteLine(new string('─', 50));

        Console.WriteLine($"  {Green}Passed:{Reset}  {result.Passed}");
        Console.WriteLine($"  {Red}Failed:{Reset}  {result.Failed}");
        Console.WriteLine($"  {Yellow}Skipped:{Reset} {result.Skipped}");
        Console.WriteLine($"  {Dim}Total:{Reset}   {result.Total}");
        Console.WriteLine($"  {Dim}Duration:{Reset} {FormatDuration(result.Duration)}");
        Console.WriteLine($"  {Dim}Pass Rate:{Reset} {result.PassRate:F1}%");

        // Show regressions if we have a previous run to compare
        if (previousRun != null)
        {
            RenderRegressions(result, previousRun);
        }

        Console.WriteLine();
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

    private static void RenderBar(TestRunResult result, int maxTotal)
    {
        var timestamp = result.Timestamp.ToString("yyyy-MM-dd HH:mm");
        var passedWidth = maxTotal > 0 ? (int)((double)result.Passed / maxTotal * BarWidth) : 0;
        var failedWidth = maxTotal > 0 ? (int)((double)result.Failed / maxTotal * BarWidth) : 0;
        var skippedWidth = maxTotal > 0 ? (int)((double)result.Skipped / maxTotal * BarWidth) : 0;

        // Ensure at least 1 char for failed if there are failures
        if (result.Failed > 0 && failedWidth == 0) failedWidth = 1;

        var passedBar = new string(PassedChar, passedWidth);
        var failedBar = new string(FailedChar, failedWidth);
        var skippedBar = new string(SkippedChar, skippedWidth);
        var emptyWidth = Math.Max(0, BarWidth - passedWidth - failedWidth - skippedWidth);
        var emptyBar = new string(EmptyChar, emptyWidth);

        var failedIndicator = result.Failed > 0
            ? $"{Red}✗{result.Failed}{Reset}"
            : $"{Green}✓{Reset}";

        Console.Write($"{Dim}{timestamp}{Reset}  ");
        Console.Write($"{Green}{passedBar}{Reset}");
        Console.Write($"{Red}{failedBar}{Reset}");
        Console.Write($"{Yellow}{skippedBar}{Reset}");
        Console.Write($"{Dim}{emptyBar}{Reset}");
        Console.Write($"  {result.Passed}/{result.Total} ({result.PassRate:F1}%)  ");
        Console.WriteLine(failedIndicator);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
            return $"{duration.TotalMinutes:F1}m";
        return $"{duration.TotalSeconds:F1}s";
    }
}
