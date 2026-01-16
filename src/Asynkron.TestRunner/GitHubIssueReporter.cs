using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace Asynkron.TestRunner;

/// <summary>
/// Reports failing tests to GitHub Issues using the gh CLI
/// </summary>
public class GitHubIssueReporter
{
    private readonly string _workingDirectory;
    private readonly List<FailedTestInfo> _failedTests = [];
    private readonly bool _verbose;
    private string? _repoUrl;

    public GitHubIssueReporter(string assemblyOrProjectPath, bool verbose = false)
    {
        // Use the directory of the assembly/project as working directory for gh
        _workingDirectory = Path.GetDirectoryName(Path.GetFullPath(assemblyOrProjectPath)) 
            ?? Environment.CurrentDirectory;
        _verbose = verbose;
    }

    /// <summary>
    /// Check if gh CLI is available
    /// </summary>
    public static bool IsGhAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return false;
            
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if we're in a GitHub repository
    /// </summary>
    public bool IsInGitHubRepo()
    {
        var (exitCode, _, _) = RunGh("repo view --json name");
        return exitCode == 0;
    }

    /// <summary>
    /// Get the GitHub repository URL
    /// </summary>
    private string? GetRepoUrl()
    {
        var (exitCode, stdout, _) = RunGh("repo view --json url --jq .url");
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            return stdout.Trim();
        }
        return null;
    }

    /// <summary>
    /// Format an issue number as a clickable link
    /// </summary>
    private string FormatIssueLink(int issueNumber)
    {
        if (_repoUrl != null)
        {
            return $"[link={_repoUrl}/issues/{issueNumber}]#{issueNumber}[/]";
        }
        return $"#{issueNumber}";
    }

    /// <summary>
    /// Add a failed test to report
    /// </summary>
    public void AddFailedTest(string fqn, string displayName, string? errorMessage, string? stackTrace, string? output)
    {
        _failedTests.Add(new FailedTestInfo
        {
            FullyQualifiedName = fqn,
            DisplayName = displayName,
            ErrorMessage = errorMessage,
            StackTrace = stackTrace,
            Output = output
        });
    }

    /// <summary>
    /// Process all failed tests - match existing issues or create new ones
    /// </summary>
    public async Task<GitHubReportResult> ReportFailuresAsync(CancellationToken ct = default)
    {
        if (_failedTests.Count == 0)
        {
            return new GitHubReportResult(0, 0, 0);
        }

        if (!IsGhAvailable())
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] gh CLI not found. Skipping GitHub issue reporting.");
            return new GitHubReportResult(0, 0, _failedTests.Count);
        }

        if (!IsInGitHubRepo())
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] Not in a GitHub repository. Skipping GitHub issue reporting.");
            return new GitHubReportResult(0, 0, _failedTests.Count);
        }

        // Sanity check: too many failures likely indicates a systemic issue, not individual test bugs
        const int maxFailuresForReporting = 20;
        if (_failedTests.Count > maxFailuresForReporting)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] {_failedTests.Count} tests failed (>{maxFailuresForReporting}). Skipping GitHub issue reporting to avoid spam.");
            return new GitHubReportResult(0, 0, _failedTests.Count);
        }

        AnsiConsole.MarkupLine($"[dim]Checking GitHub issues for {_failedTests.Count} failed tests...[/]");

        // Get repo URL for links
        _repoUrl = GetRepoUrl();

        // Get existing open issues
        var existingIssues = await GetOpenIssuesAsync(ct);
        
        var matched = 0;
        var created = 0;
        var skipped = 0;

        foreach (var test in _failedTests)
        {
            ct.ThrowIfCancellationRequested();

            var matchingIssue = FindMatchingIssue(test, existingIssues);
            
            if (matchingIssue != null)
            {
                if (_verbose)
                {
                    var existingUrl = FormatIssueLink(matchingIssue.Number);
                    AnsiConsole.MarkupLine($"[dim]  Found existing issue {existingUrl} for {test.DisplayName}[/]");
                }
                matched++;
            }
            else
            {
                // Create new issue
                var issueNumber = await CreateIssueAsync(test, ct);
                if (issueNumber > 0)
                {
                    var issueUrl = FormatIssueLink(issueNumber);
                    AnsiConsole.MarkupLine($"[green]  Created issue {issueUrl}[/] for [blue]{test.DisplayName}[/]");
                    created++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        return new GitHubReportResult(matched, created, skipped);
    }

    private async Task<List<GitHubIssue>> GetOpenIssuesAsync(CancellationToken ct)
    {
        var issues = new List<GitHubIssue>();
        
        // Get open issues (limit to recent ones for performance)
        var (exitCode, stdout, _) = RunGh("issue list --state open --limit 500 --json number,title,body,comments");
        
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return issues;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<GitHubIssueJson>>(stdout, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed != null)
            {
                foreach (var issue in parsed)
                {
                    issues.Add(new GitHubIssue
                    {
                        Number = issue.Number,
                        Title = issue.Title ?? "",
                        Body = issue.Body ?? "",
                        Comments = issue.Comments?.Select(c => c.Body ?? "").ToList() ?? []
                    });
                }
            }
        }
        catch (JsonException ex)
        {
            if (_verbose)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Failed to parse GitHub issues: {ex.Message}");
            }
        }

        return issues;
    }

    private static GitHubIssue? FindMatchingIssue(FailedTestInfo test, List<GitHubIssue> issues)
    {
        // Parse test FQN into parts for matching
        var parts = ParseTestFqn(test.FullyQualifiedName);
        
        foreach (var issue in issues)
        {
            // Check if issue title contains test name or vice versa
            if (IsTestMatch(test.FullyQualifiedName, test.DisplayName, parts, issue.Title))
            {
                return issue;
            }

            // Check issue body
            if (IsTestMatch(test.FullyQualifiedName, test.DisplayName, parts, issue.Body))
            {
                return issue;
            }

            // Check comments
            foreach (var comment in issue.Comments)
            {
                if (IsTestMatch(test.FullyQualifiedName, test.DisplayName, parts, comment))
                {
                    return issue;
                }
            }
        }

        return null;
    }

    private static bool IsTestMatch(string fqn, string displayName, TestFqnParts parts, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // Direct substring match (either direction)
        if (text.Contains(fqn, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (fqn.Contains(text, StringComparison.OrdinalIgnoreCase) && text.Length > 10)
        {
            return true;
        }

        // Check display name
        if (!string.IsNullOrEmpty(displayName) && text.Contains(displayName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if multiple parts match (namespace + class + method all present)
        var matchCount = 0;
        if (!string.IsNullOrEmpty(parts.Namespace) && text.Contains(parts.Namespace, StringComparison.OrdinalIgnoreCase))
        {
            matchCount++;
        }
        if (!string.IsNullOrEmpty(parts.ClassName) && text.Contains(parts.ClassName, StringComparison.OrdinalIgnoreCase))
        {
            matchCount++;
        }
        if (!string.IsNullOrEmpty(parts.MethodName) && text.Contains(parts.MethodName, StringComparison.OrdinalIgnoreCase))
        {
            matchCount++;
        }

        // If class and method both match, consider it a match
        return matchCount >= 2 && !string.IsNullOrEmpty(parts.MethodName) && 
               text.Contains(parts.MethodName, StringComparison.OrdinalIgnoreCase);
    }

    private static TestFqnParts ParseTestFqn(string fqn)
    {
        // Remove parameters: "Namespace.Class.Method(args)" -> "Namespace.Class.Method"
        var parenIndex = fqn.IndexOf('(');
        var baseFqn = parenIndex > 0 ? fqn[..parenIndex] : fqn;
        
        var parts = baseFqn.Split('.');
        
        return parts.Length switch
        {
            0 => new TestFqnParts("", "", ""),
            1 => new TestFqnParts("", "", parts[0]),
            2 => new TestFqnParts("", parts[0], parts[1]),
            _ => new TestFqnParts(
                string.Join(".", parts[..^2]),
                parts[^2],
                parts[^1])
        };
    }

    private async Task<int> CreateIssueAsync(FailedTestInfo test, CancellationToken ct)
    {
        var title = $"Failing Test: {test.DisplayName}";
        if (title.Length > 200)
        {
            title = title[..197] + "...";
        }

        var body = BuildIssueBody(test);
        
        // Use heredoc-style input to handle special characters
        var bodyFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(bodyFile, body, ct);
            
            var (exitCode, stdout, stderr) = RunGh($"issue create --title \"{EscapeShellArg(title)}\" --body-file \"{bodyFile}\"");
            
            if (exitCode != 0)
            {
                if (_verbose)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] Failed to create issue: {stderr}");
                }
                return 0;
            }

            // Parse issue number from output (usually contains URL like https://github.com/owner/repo/issues/123)
            var match = System.Text.RegularExpressions.Regex.Match(stdout, @"/issues/(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var issueNumber))
            {
                return issueNumber;
            }

            // Try to parse just a number
            if (int.TryParse(stdout.Trim(), out issueNumber))
            {
                return issueNumber;
            }

            return -1; // Created but couldn't parse number
        }
        finally
        {
            try { File.Delete(bodyFile); } catch { }
        }
    }

    private string BuildIssueBody(FailedTestInfo test)
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("## Failing Test");
        sb.AppendLine();
        sb.AppendLine($"**Test:** `{test.FullyQualifiedName}`");
        sb.AppendLine($"**Display Name:** {test.DisplayName}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(test.ErrorMessage))
        {
            sb.AppendLine("## Error Message");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(test.ErrorMessage);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(test.StackTrace))
        {
            sb.AppendLine("## Stack Trace");
            sb.AppendLine();
            sb.AppendLine("```");
            // Truncate stack trace if too long
            var stackTrace = test.StackTrace;
            if (stackTrace.Length > 5000)
            {
                stackTrace = stackTrace[..5000] + "\n... (truncated)";
            }
            sb.AppendLine(stackTrace);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(test.Output))
        {
            sb.AppendLine("## Test Output");
            sb.AppendLine();
            sb.AppendLine("<details>");
            sb.AppendLine("<summary>Click to expand</summary>");
            sb.AppendLine();
            sb.AppendLine("```");
            var output = test.Output;
            if (output.Length > 3000)
            {
                output = output[..3000] + "\n... (truncated)";
            }
            sb.AppendLine(output);
            sb.AppendLine("```");
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        // Add other failing tests in the same run
        var otherFailures = _failedTests
            .Where(t => t.FullyQualifiedName != test.FullyQualifiedName)
            .Take(10)
            .ToList();

        if (otherFailures.Count > 0)
        {
            sb.AppendLine("## Other Failing Tests in This Run");
            sb.AppendLine();
            foreach (var other in otherFailures)
            {
                sb.AppendLine($"- `{other.DisplayName}`");
            }
            if (_failedTests.Count > 11)
            {
                sb.AppendLine($"- ... and {_failedTests.Count - 11} more");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine($"*Generated by Asynkron.TestRunner at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*");

        return sb.ToString();
    }

    private (int ExitCode, string Stdout, string Stderr) RunGh(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "gh",
                Arguments = arguments,
                WorkingDirectory = _workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (-1, "", "Failed to start gh process");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30000);

            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    private static string EscapeShellArg(string arg)
    {
        return arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private class FailedTestInfo
    {
        public string FullyQualifiedName { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string? ErrorMessage { get; init; }
        public string? StackTrace { get; init; }
        public string? Output { get; init; }
    }

    private class GitHubIssue
    {
        public int Number { get; init; }
        public string Title { get; init; } = "";
        public string Body { get; init; } = "";
        public List<string> Comments { get; init; } = [];
    }

    private class GitHubIssueJson
    {
        public int Number { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public List<GitHubCommentJson>? Comments { get; set; }
    }

    private class GitHubCommentJson
    {
        public string? Body { get; set; }
    }

    private record TestFqnParts(string Namespace, string ClassName, string MethodName);
}

public record GitHubReportResult(int Matched, int Created, int Skipped);
