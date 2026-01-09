using System.Reflection;
using Asynkron.TestRunner.Models;

namespace Asynkron.TestRunner;

/// <summary>
/// Filter criteria for test discovery
/// </summary>
public class TestFilter
{
    public string? Namespace { get; set; }
    public string? Class { get; set; }
    public string? Method { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>
    /// Parse a filter string like "Class=LanguageTests" or "Namespace=Tests"
    /// </summary>
    public static TestFilter Parse(string? filterString)
    {
        var filter = new TestFilter();
        if (string.IsNullOrWhiteSpace(filterString))
            return filter;

        // Check for key=value patterns
        var parts = filterString.Split('=', 2);
        if (parts.Length == 2)
        {
            var key = parts[0].Trim().ToLowerInvariant();
            var value = parts[1].Trim();

            switch (key)
            {
                case "namespace":
                case "ns":
                    filter.Namespace = value;
                    break;
                case "class":
                case "classname":
                    filter.Class = value;
                    break;
                case "method":
                case "methodname":
                    filter.Method = value;
                    break;
                case "displayname":
                case "name":
                    filter.DisplayName = value;
                    break;
                default:
                    // Unknown key, treat as class filter
                    filter.Class = filterString;
                    break;
            }
        }
        else
        {
            // No key=value, treat as class filter (most common use case)
            filter.Class = filterString;
        }

        return filter;
    }

    public bool Matches(DiscoveredTest test)
    {
        if (Namespace != null && !test.Namespace.Contains(Namespace, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Class != null && !test.ClassName.Equals(Class, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Method != null && !test.MethodName.Contains(Method, StringComparison.OrdinalIgnoreCase))
            return false;

        if (DisplayName != null && !test.DisplayName.Contains(DisplayName, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public bool IsEmpty => Namespace == null && Class == null && Method == null && DisplayName == null;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Namespace != null) parts.Add($"Namespace={Namespace}");
        if (Class != null) parts.Add($"Class={Class}");
        if (Method != null) parts.Add($"Method={Method}");
        if (DisplayName != null) parts.Add($"DisplayName={DisplayName}");
        return parts.Count > 0 ? string.Join(", ", parts) : "(no filter)";
    }
}

/// <summary>
/// Represents a discovered test with full metadata from VSTest platform
/// </summary>
public class DiscoveredTest
{
    public required string FullyQualifiedName { get; init; }
    public required string DisplayName { get; init; }
    public required string Namespace { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public required string Source { get; init; }
    public Guid Id { get; init; }

    /// <summary>
    /// Get the vstest filter string to run this specific test
    /// </summary>
    public string GetVsTestFilter()
    {
        return $"FullyQualifiedName={EscapeFilter(FullyQualifiedName)}";
    }

    private static string EscapeFilter(string value)
    {
        return value.Replace("(", "\\(").Replace(")", "\\)");
    }

}

/// <summary>
/// Discovers tests using reflection on test assemblies
/// </summary>
public class TestDiscovery
{
    // Known test method attributes
    private static readonly HashSet<string> TestMethodAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TestAttribute",
        "FactAttribute",
        "TheoryAttribute",
        "TestMethodAttribute",
        "TestCaseAttribute",
        "TestCaseSourceAttribute"
    };

    /// <summary>
    /// Discovers all tests in the given assembly paths using reflection
    /// </summary>
    public static async Task<List<DiscoveredTest>> DiscoverTestsAsync(
        IEnumerable<string> assemblyPaths,
        TestFilter? filter = null)
    {
        var allTests = new List<DiscoveredTest>();

        foreach (var assemblyPath in assemblyPaths)
        {
            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine($"Warning: Assembly not found: {assemblyPath}");
                continue;
            }

            try
            {
                var tests = await Task.Run(() => DiscoverTestsInAssembly(assemblyPath));
                allTests.AddRange(tests);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error discovering tests in {assemblyPath}: {ex.Message}");
            }
        }

        // Apply filter if specified
        if (filter != null && !filter.IsEmpty)
        {
            allTests = allTests.Where(t => filter.Matches(t)).ToList();
        }

        return allTests;
    }

    private static List<DiscoveredTest> DiscoverTestsInAssembly(string assemblyPath)
    {
        var tests = new List<DiscoveredTest>();

        // Get runtime directory for core assemblies
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var assemblyDir = Path.GetDirectoryName(assemblyPath)!;

        // Collect all assemblies we might need
        var assemblyPaths = new List<string> { assemblyPath };
        assemblyPaths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
        assemblyPaths.AddRange(Directory.GetFiles(assemblyDir, "*.dll"));

        var resolver = new PathAssemblyResolver(assemblyPaths.Distinct());
        using var mlc = new MetadataLoadContext(resolver, "System.Private.CoreLib");

        try
        {
            var assembly = mlc.LoadFromAssemblyPath(assemblyPath);

            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsClass || type.IsAbstract)
                    continue;

                var className = type.Name;
                var namespaceName = type.Namespace ?? "";

                // Find test methods
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var hasTestAttribute = method.CustomAttributes.Any(attr =>
                        TestMethodAttributes.Contains(attr.AttributeType.Name));

                    if (!hasTestAttribute)
                        continue;

                    // Check for test cases (parameterized tests)
                    var testCases = GetTestCases(method);

                    if (testCases.Count > 0)
                    {
                        // Parameterized test - create a DiscoveredTest for each case
                        foreach (var testCase in testCases)
                        {
                            var displayName = $"{method.Name}({testCase})";
                            tests.Add(new DiscoveredTest
                            {
                                FullyQualifiedName = $"{namespaceName}.{className}.{method.Name}",
                                DisplayName = displayName,
                                Namespace = namespaceName,
                                ClassName = className,
                                MethodName = method.Name,
                                Source = assemblyPath,
                                Id = Guid.NewGuid()
                            });
                        }
                    }
                    else
                    {
                        // Simple test method
                        tests.Add(new DiscoveredTest
                        {
                            FullyQualifiedName = $"{namespaceName}.{className}.{method.Name}",
                            DisplayName = method.Name,
                            Namespace = namespaceName,
                            ClassName = className,
                            MethodName = method.Name,
                            Source = assemblyPath,
                            Id = Guid.NewGuid()
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading assembly {assemblyPath}: {ex.Message}");
        }

        return tests;
    }

    private static List<string> GetTestCases(MethodInfo method)
    {
        var cases = new List<string>();

        foreach (var attr in method.CustomAttributes)
        {
            if (attr.AttributeType.Name == "TestCaseAttribute")
            {
                // Get constructor arguments as test case parameters
                var args = attr.ConstructorArguments
                    .Select(arg => FormatArgumentValue(arg.Value))
                    .ToList();

                cases.Add(string.Join(",", args));
            }
        }

        return cases;
    }

    private static string FormatArgumentValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b.ToString(),
            _ => value.ToString() ?? "null"
        };
    }

    // Keep the old API for backwards compatibility
    public static async Task<List<TestAssemblyDescriptor>> DiscoverTestsAsync(IEnumerable<string> assemblyPaths)
    {
        var tests = await DiscoverTestsAsync(assemblyPaths, null);
        return ConvertToLegacyFormat(tests);
    }

    private static List<TestAssemblyDescriptor> ConvertToLegacyFormat(List<DiscoveredTest> tests)
    {
        var byAssembly = tests.GroupBy(t => t.Source);
        var results = new List<TestAssemblyDescriptor>();

        foreach (var assemblyGroup in byAssembly)
        {
            var namespaceGroups = new Dictionary<string, TestNamespaceDescriptor>();

            foreach (var test in assemblyGroup)
            {
                if (!namespaceGroups.TryGetValue(test.Namespace, out var namespaceDesc))
                {
                    namespaceDesc = new TestNamespaceDescriptor { Namespace = test.Namespace };
                    namespaceGroups[test.Namespace] = namespaceDesc;
                }

                var classKey = $"{test.Namespace}.{test.ClassName}";
                var testClass = namespaceDesc.Classes.FirstOrDefault(c => c.FullyQualifiedName == classKey);

                if (testClass == null)
                {
                    testClass = new TestClassDescriptor
                    {
                        Namespace = test.Namespace,
                        ClassName = test.ClassName,
                        FullyQualifiedName = classKey
                    };
                    namespaceDesc.Classes.Add(testClass);
                }

                var existingMethod = testClass.Methods.FirstOrDefault(m => m.MethodName == test.MethodName);
                if (existingMethod != null)
                {
                    var updatedMethod = new TestDescriptor
                    {
                        Namespace = existingMethod.Namespace,
                        ClassName = existingMethod.ClassName,
                        MethodName = existingMethod.MethodName,
                        FullyQualifiedName = existingMethod.FullyQualifiedName,
                        Framework = existingMethod.Framework,
                        Type = TestType.Theory,
                        TestCaseCount = existingMethod.TestCaseCount + 1,
                        DisplayName = existingMethod.DisplayName,
                        SkipReason = existingMethod.SkipReason,
                        Traits = existingMethod.Traits
                    };
                    testClass.Methods.Remove(existingMethod);
                    testClass.Methods.Add(updatedMethod);
                }
                else
                {
                    testClass.Methods.Add(new TestDescriptor
                    {
                        Namespace = test.Namespace,
                        ClassName = test.ClassName,
                        MethodName = test.MethodName,
                        FullyQualifiedName = test.FullyQualifiedName,
                        Framework = TestFramework.Unknown,
                        Type = TestType.Fact,
                        TestCaseCount = 1,
                        DisplayName = test.DisplayName,
                        SkipReason = null,
                        Traits = new Dictionary<string, string>()
                    });
                }
            }

            results.Add(new TestAssemblyDescriptor
            {
                AssemblyPath = assemblyGroup.Key,
                AssemblyName = Path.GetFileNameWithoutExtension(assemblyGroup.Key),
                Namespaces = namespaceGroups.Values.OrderBy(n => n.Namespace).ToList()
            });
        }

        return results;
    }
}
