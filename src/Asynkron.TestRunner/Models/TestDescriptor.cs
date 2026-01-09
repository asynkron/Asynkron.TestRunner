namespace Asynkron.TestRunner.Models;

/// <summary>
/// Represents a discovered test method with its metadata
/// </summary>
public class TestDescriptor
{
    public required string Namespace { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public required string FullyQualifiedName { get; init; }
    public required TestFramework Framework { get; init; }
    public required TestType Type { get; init; }

    /// <summary>
    /// For theory/parameterized tests, the number of test cases
    /// For facts, this is 1
    /// </summary>
    public int TestCaseCount { get; init; } = 1;

    /// <summary>
    /// Display name if specified by [DisplayName] or similar
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Skip reason if test is marked with [Skip]
    /// </summary>
    public string? SkipReason { get; init; }

    /// <summary>
    /// Traits/categories/tags
    /// </summary>
    public Dictionary<string, string> Traits { get; init; } = new();
}

/// <summary>
/// Represents a test class with its tests
/// </summary>
public class TestClassDescriptor
{
    public required string Namespace { get; init; }
    public required string ClassName { get; init; }
    public required string FullyQualifiedName { get; init; }
    public List<TestDescriptor> Methods { get; init; } = new();
    public int TotalTestCases => Methods.Sum(m => m.TestCaseCount);
}

/// <summary>
/// Represents a namespace containing test classes
/// </summary>
public class TestNamespaceDescriptor
{
    public required string Namespace { get; init; }
    public List<TestClassDescriptor> Classes { get; init; } = new();
    public int TotalTestCases => Classes.Sum(c => c.TotalTestCases);
}

/// <summary>
/// Results of test discovery for an assembly
/// </summary>
public class TestAssemblyDescriptor
{
    public required string AssemblyPath { get; init; }
    public required string AssemblyName { get; init; }
    public List<TestNamespaceDescriptor> Namespaces { get; init; } = new();
    public int TotalTestCases => Namespaces.Sum(n => n.TotalTestCases);
}

public enum TestFramework
{
    XUnit,
    NUnit,
    MSTest,
    Unknown
}

public enum TestType
{
    Fact,      // xUnit [Fact] or NUnit [Test]
    Theory,    // xUnit [Theory] or NUnit [TestCase]
    Unknown
}
