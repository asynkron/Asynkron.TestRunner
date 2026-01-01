using Asynkron.TestRunner.Models;

namespace Asynkron.TestRunner;

/// <summary>
/// Represents different timeout strategies for test execution.
/// </summary>
public enum TimeoutMode
{
    /// <summary>
    /// Fixed timeout for all tests (default: 20s).
    /// </summary>
    Fixed,

    /// <summary>
    /// Adaptive timeout based on historical test durations.
    /// Uses median duration * multiplier, with a minimum floor.
    /// </summary>
    Adaptive,

    /// <summary>
    /// Graduated timeout that increases for retries.
    /// First run uses base timeout, retries use increasing multipliers.
    /// </summary>
    Graduated,

    /// <summary>
    /// No timeout - tests run until completion or manual termination.
    /// </summary>
    None
}

/// <summary>
/// Provides timeout calculation strategies for test execution.
/// </summary>
public class TimeoutStrategy
{
    private const int DefaultTimeoutSeconds = 20;
    private const int MinAdaptiveTimeoutSeconds = 5;
    private const double AdaptiveMultiplier = 3.0; // Median * 3 for headroom

    private readonly TimeoutMode _mode;
    private readonly int _baseTimeoutSeconds;
    private readonly ResultStore? _store;

    public TimeoutMode Mode => _mode;
    public int BaseTimeoutSeconds => _baseTimeoutSeconds;

    public TimeoutStrategy(TimeoutMode mode = TimeoutMode.Fixed, int? baseTimeoutSeconds = null, ResultStore? store = null)
    {
        _mode = mode;
        _baseTimeoutSeconds = baseTimeoutSeconds ?? DefaultTimeoutSeconds;
        _store = store;
    }

    /// <summary>
    /// Gets the timeout for an initial test run (not a retry).
    /// </summary>
    public int GetTimeout()
    {
        return GetTimeout(attemptNumber: 1);
    }

    /// <summary>
    /// Gets the timeout for a specific attempt number.
    /// </summary>
    /// <param name="attemptNumber">The attempt number (1 = first attempt, 2+ = retries).</param>
    public int GetTimeout(int attemptNumber)
    {
        return _mode switch
        {
            TimeoutMode.None => 0,
            TimeoutMode.Fixed => _baseTimeoutSeconds,
            TimeoutMode.Adaptive => CalculateAdaptiveTimeout(),
            TimeoutMode.Graduated => CalculateGraduatedTimeout(attemptNumber),
            _ => _baseTimeoutSeconds
        };
    }

    /// <summary>
    /// Gets the timeout for a batch of tests based on count.
    /// </summary>
    /// <param name="testCount">Number of tests in the batch.</param>
    /// <param name="attemptNumber">The attempt number.</param>
    public int GetBatchTimeout(int testCount, int attemptNumber = 1)
    {
        var perTestTimeout = GetTimeout(attemptNumber);
        if (perTestTimeout == 0)
            return 0;

        // For batches, we use a formula that accounts for:
        // - Per-test timeout as a ceiling for any single test
        // - Some overhead for test setup/teardown
        // - Parallelism (tests may run in parallel)

        // Estimate: max(perTest * 2, perTest + sqrt(testCount) * perTest / 4)
        var baseTimeout = Math.Max(
            perTestTimeout * 2,
            perTestTimeout + (int)(Math.Sqrt(testCount) * perTestTimeout / 4));

        return Math.Min(baseTimeout, perTestTimeout * testCount);
    }

    private int CalculateAdaptiveTimeout()
    {
        if (_store == null)
            return _baseTimeoutSeconds;

        var recentRuns = _store.GetRecentRuns(5);
        if (recentRuns.Count == 0)
            return _baseTimeoutSeconds;

        // Get the median duration per test
        var durations = recentRuns
            .Where(r => r.Total > 0)
            .Select(r => r.Duration.TotalSeconds / r.Total)
            .OrderBy(d => d)
            .ToList();

        if (durations.Count == 0)
            return _baseTimeoutSeconds;

        var medianDuration = durations[durations.Count / 2];
        var adaptiveTimeout = (int)(medianDuration * AdaptiveMultiplier);

        // Apply floor and ceiling
        return Math.Max(MinAdaptiveTimeoutSeconds, Math.Min(adaptiveTimeout, _baseTimeoutSeconds * 5));
    }

    private int CalculateGraduatedTimeout(int attemptNumber)
    {
        // First attempt: base timeout
        // Second attempt: base * 2
        // Third attempt: base * 4
        // etc.
        var multiplier = Math.Pow(2, attemptNumber - 1);
        return (int)(_baseTimeoutSeconds * multiplier);
    }

    /// <summary>
    /// Creates a timeout strategy from command-line options.
    /// </summary>
    public static TimeoutStrategy FromOptions(string? mode, int? timeoutSeconds, ResultStore? store)
    {
        var timeoutMode = mode?.ToLowerInvariant() switch
        {
            "fixed" => TimeoutMode.Fixed,
            "adaptive" => TimeoutMode.Adaptive,
            "graduated" => TimeoutMode.Graduated,
            "none" or "0" => TimeoutMode.None,
            _ => TimeoutMode.Fixed
        };

        return new TimeoutStrategy(timeoutMode, timeoutSeconds, store);
    }

    /// <summary>
    /// Provides a human-readable description of the current timeout configuration.
    /// </summary>
    public string GetDescription()
    {
        return _mode switch
        {
            TimeoutMode.None => "No timeout (tests run until completion)",
            TimeoutMode.Fixed => $"Fixed timeout: {_baseTimeoutSeconds}s per test",
            TimeoutMode.Adaptive => $"Adaptive timeout: based on historical duration (base: {_baseTimeoutSeconds}s)",
            TimeoutMode.Graduated => $"Graduated timeout: {_baseTimeoutSeconds}s (doubles on retry)",
            _ => $"Timeout: {_baseTimeoutSeconds}s"
        };
    }
}
