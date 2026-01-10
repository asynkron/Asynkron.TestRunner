namespace Asynkron.TestRunner.Worker;

/// <summary>
/// Abstraction for test framework runners (xUnit, NUnit, MSTest)
/// </summary>
public interface ITestFramework
{
    /// <summary>
    /// Check if this framework can handle the given assembly
    /// </summary>
    bool CanHandle(string assemblyPath);

    /// <summary>
    /// Discover tests in the assembly
    /// </summary>
    IEnumerable<TestInfo> Discover(string assemblyPath);

    /// <summary>
    /// Run tests and stream results
    /// </summary>
    IAsyncEnumerable<TestResult> RunAsync(
        string assemblyPath,
        IEnumerable<string>? testFqns,  // null = run all
        CancellationToken ct = default);
}

/// <summary>
/// Information about a discovered test
/// </summary>
public record TestInfo(
    string FullyQualifiedName,
    string DisplayName,
    string? SkipReason = null
);

/// <summary>
/// Result of running a single test
/// </summary>
public abstract record TestResult(
    string FullyQualifiedName,
    string DisplayName
);

public record TestStarted(
    string FullyQualifiedName,
    string DisplayName
) : TestResult(FullyQualifiedName, DisplayName);

public record TestPassed(
    string FullyQualifiedName,
    string DisplayName,
    TimeSpan Duration
) : TestResult(FullyQualifiedName, DisplayName);

public record TestFailed(
    string FullyQualifiedName,
    string DisplayName,
    TimeSpan Duration,
    string ErrorMessage,
    string? StackTrace = null
) : TestResult(FullyQualifiedName, DisplayName);

public record TestSkipped(
    string FullyQualifiedName,
    string DisplayName,
    string? Reason = null
) : TestResult(FullyQualifiedName, DisplayName);

public record TestOutput(
    string FullyQualifiedName,
    string Text
) : TestResult(FullyQualifiedName, "");
