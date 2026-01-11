using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asynkron.TestRunner.Protocol;

/// <summary>
/// Base class for all protocol messages (JSON lines over stdio)
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DiscoverCommand), "discover")]
[JsonDerivedType(typeof(RunCommand), "run")]
[JsonDerivedType(typeof(CancelCommand), "cancel")]
[JsonDerivedType(typeof(DiscoveredEvent), "discovered")]
[JsonDerivedType(typeof(TestStartedEvent), "started")]
[JsonDerivedType(typeof(TestPassedEvent), "passed")]
[JsonDerivedType(typeof(TestFailedEvent), "failed")]
[JsonDerivedType(typeof(TestSkippedEvent), "skipped")]
[JsonDerivedType(typeof(TestOutputEvent), "output")]
[JsonDerivedType(typeof(RunCompletedEvent), "completed")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
public abstract record ProtocolMessage;

#region Commands (Coordinator → Worker)

/// <summary>
/// Discover tests in the specified assembly
/// </summary>
public record DiscoverCommand(string Assembly) : ProtocolMessage;

/// <summary>
/// Run specific tests with optional timeout
/// </summary>
public record RunCommand(
    string Assembly,
    List<string>? Tests = null,      // null = run all, otherwise FQNs to run
    int? TimeoutSeconds = null        // per-test timeout, null = no timeout
) : ProtocolMessage;

/// <summary>
/// Cancel the current operation
/// </summary>
public record CancelCommand : ProtocolMessage;

#endregion

#region Events (Worker → Coordinator)

/// <summary>
/// Test discovery completed
/// </summary>
public record DiscoveredEvent(List<DiscoveredTestInfo> Tests) : ProtocolMessage;

/// <summary>
/// A test has started executing
/// </summary>
public record TestStartedEvent(string FullyQualifiedName, string DisplayName) : ProtocolMessage;

/// <summary>
/// A test passed
/// </summary>
public record TestPassedEvent(
    string FullyQualifiedName,
    string DisplayName,
    double DurationMs
) : ProtocolMessage;

/// <summary>
/// A test failed
/// </summary>
public record TestFailedEvent(
    string FullyQualifiedName,
    string DisplayName,
    double DurationMs,
    string ErrorMessage,
    string? StackTrace = null
) : ProtocolMessage;

/// <summary>
/// A test was skipped
/// </summary>
public record TestSkippedEvent(
    string FullyQualifiedName,
    string DisplayName,
    string? Reason = null
) : ProtocolMessage;

/// <summary>
/// Test output (stdout/stderr from test)
/// </summary>
public record TestOutputEvent(
    string FullyQualifiedName,
    string Text
) : ProtocolMessage;

/// <summary>
/// All tests completed
/// </summary>
public record RunCompletedEvent(
    int Passed,
    int Failed,
    int Skipped,
    double TotalDurationMs
) : ProtocolMessage;

/// <summary>
/// An error occurred in the worker
/// </summary>
public record ErrorEvent(string Message, string? Details = null) : ProtocolMessage;

#endregion

#region Supporting Types

/// <summary>
/// Information about a discovered test
/// </summary>
public record DiscoveredTestInfo(
    string FullyQualifiedName,
    string DisplayName,
    string? SkipReason = null
);

#endregion

/// <summary>
/// JSON serialization helpers for protocol messages
/// </summary>
public static class ProtocolIO
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serialize a message to a JSON line
    /// </summary>
    public static string Serialize(ProtocolMessage message)
    {
        return JsonSerializer.Serialize(message, Options);
    }

    /// <summary>
    /// Deserialize a JSON line to a message
    /// </summary>
    public static ProtocolMessage? Deserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProtocolMessage>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Write a message to a TextWriter (adds newline)
    /// </summary>
    public static void Write(TextWriter writer, ProtocolMessage message)
    {
        writer.WriteLine(Serialize(message));
        writer.Flush();
    }

    /// <summary>
    /// Read a message from a TextReader
    /// </summary>
    public static async Task<ProtocolMessage?> ReadAsync(TextReader reader, CancellationToken ct = default)
    {
        var line = await reader.ReadLineAsync(ct);
        return line == null ? null : Deserialize(line);
    }
}
