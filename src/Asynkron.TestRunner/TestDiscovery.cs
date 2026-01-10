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

    public static TestFilter Parse(string? filterString)
    {
        var filter = new TestFilter();
        if (string.IsNullOrWhiteSpace(filterString))
            return filter;

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
                    filter.Class = filterString;
                    break;
            }
        }
        else
        {
            filter.Class = filterString;
        }

        return filter;
    }

    public bool Matches(DiscoveredTest test)
    {
        if (Namespace != null && !test.Namespace.Contains(Namespace, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Class != null && !test.ClassName.Contains(Class, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Method != null && !test.MethodName.Contains(Method, StringComparison.OrdinalIgnoreCase))
            return false;

        if (DisplayName != null && !test.DisplayName.Contains(DisplayName, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Matches against FQN and display name (for worker discovery results)
    /// </summary>
    public bool Matches(string fullyQualifiedName, string displayName)
    {
        // FQN format: Namespace.Class.Method
        if (Namespace != null && !fullyQualifiedName.Contains(Namespace, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Class != null && !fullyQualifiedName.Contains(Class, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Method != null && !fullyQualifiedName.Contains(Method, StringComparison.OrdinalIgnoreCase))
            return false;

        if (DisplayName != null && !displayName.Contains(DisplayName, StringComparison.OrdinalIgnoreCase))
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
/// Represents a discovered test with full metadata
/// </summary>
public class DiscoveredTest
{
    public required string FullyQualifiedName { get; init; }
    public required string DisplayName { get; init; }
    public required string Namespace { get; init; }
    public required string ClassName { get; init; }
    public required string MethodName { get; init; }
    public required string Source { get; init; }
    public required TestFramework Framework { get; init; }
    public required TestType TestType { get; init; }
    public string? SkipReason { get; init; }
    public string? TestCaseArgs { get; init; }  // For parameterized tests
    public Guid Id { get; init; } = Guid.NewGuid();
}

/// <summary>
/// Discovers tests using reflection on test assemblies
/// </summary>
public static class TestDiscovery
{
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

        if (filter != null && !filter.IsEmpty)
        {
            allTests = allTests.Where(t => filter.Matches(t)).ToList();
        }

        return allTests;
    }

    private static List<DiscoveredTest> DiscoverTestsInAssembly(string assemblyPath)
    {
        var tests = new List<DiscoveredTest>();

        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var assemblyDir = Path.GetDirectoryName(assemblyPath)!;

        var paths = new List<string> { assemblyPath };
        paths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
        paths.AddRange(Directory.GetFiles(assemblyDir, "*.dll"));

        var resolver = new PathAssemblyResolver(paths.Distinct());
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

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var testInfo = AnalyzeTestMethod(method);
                    if (testInfo == null)
                        continue;

                    var (framework, testType, skipReason, testCases) = testInfo.Value;
                    var fqn = $"{namespaceName}.{className}.{method.Name}";

                    if (testCases.Count > 0)
                    {
                        // Parameterized test - create entry for each test case
                        foreach (var testCase in testCases)
                        {
                            tests.Add(new DiscoveredTest
                            {
                                FullyQualifiedName = fqn,
                                DisplayName = $"{method.Name}({testCase})",
                                Namespace = namespaceName,
                                ClassName = className,
                                MethodName = method.Name,
                                Source = assemblyPath,
                                Framework = framework,
                                TestType = testType,
                                SkipReason = skipReason,
                                TestCaseArgs = testCase
                            });
                        }
                    }
                    else
                    {
                        // Simple test
                        tests.Add(new DiscoveredTest
                        {
                            FullyQualifiedName = fqn,
                            DisplayName = method.Name,
                            Namespace = namespaceName,
                            ClassName = className,
                            MethodName = method.Name,
                            Source = assemblyPath,
                            Framework = framework,
                            TestType = testType,
                            SkipReason = skipReason
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

    private static (TestFramework Framework, TestType Type, string? SkipReason, List<string> TestCases)?
        AnalyzeTestMethod(MethodInfo method)
    {
        TestFramework? framework = null;
        TestType testType = TestType.Fact;
        string? skipReason = null;
        var testCases = new List<string>();

        foreach (var attr in method.CustomAttributes)
        {
            var attrName = attr.AttributeType.Name;

            switch (attrName)
            {
                // xUnit
                case "FactAttribute":
                    framework = TestFramework.XUnit;
                    testType = TestType.Fact;
                    skipReason = GetNamedArgument<string>(attr, "Skip");
                    break;

                case "TheoryAttribute":
                    framework = TestFramework.XUnit;
                    testType = TestType.Theory;
                    skipReason = GetNamedArgument<string>(attr, "Skip");
                    break;

                case "InlineDataAttribute":
                    // xUnit parameterized data
                    var inlineArgs = attr.ConstructorArguments
                        .SelectMany(a => a.Value is IEnumerable<CustomAttributeTypedArgument> arr
                            ? arr.Select(x => FormatArgValue(x.Value))
                            : [FormatArgValue(a.Value)])
                        .ToList();
                    testCases.Add(string.Join(", ", inlineArgs));
                    break;

                // NUnit
                case "TestAttribute":
                    framework = TestFramework.NUnit;
                    testType = TestType.Fact;
                    break;

                case "TestCaseAttribute":
                    framework = TestFramework.NUnit;
                    testType = TestType.Theory;
                    var nunitArgs = attr.ConstructorArguments
                        .Select(a => FormatArgValue(a.Value))
                        .ToList();
                    testCases.Add(string.Join(", ", nunitArgs));
                    break;

                case "TestCaseSourceAttribute":
                    framework = TestFramework.NUnit;
                    testType = TestType.Theory;
                    // Can't resolve actual values at discovery time, mark as dynamic
                    if (testCases.Count == 0)
                        testCases.Add("...");  // Placeholder for dynamic cases
                    break;

                case "IgnoreAttribute":
                    // NUnit skip
                    skipReason = GetConstructorArgument<string>(attr, 0) ?? "Ignored";
                    break;

                // MSTest
                case "TestMethodAttribute":
                    framework = TestFramework.MSTest;
                    testType = TestType.Fact;
                    break;

                case "DataRowAttribute":
                    framework = TestFramework.MSTest;
                    testType = TestType.Theory;
                    var msArgs = attr.ConstructorArguments
                        .Select(a => FormatArgValue(a.Value))
                        .ToList();
                    testCases.Add(string.Join(", ", msArgs));
                    break;
            }
        }

        if (framework == null)
            return null;

        return (framework.Value, testType, skipReason, testCases);
    }

    private static T? GetNamedArgument<T>(CustomAttributeData attr, string name)
    {
        var arg = attr.NamedArguments.FirstOrDefault(a => a.MemberName == name);
        if (arg.TypedValue.Value is T value)
            return value;
        return default;
    }

    private static T? GetConstructorArgument<T>(CustomAttributeData attr, int index)
    {
        if (index < attr.ConstructorArguments.Count && attr.ConstructorArguments[index].Value is T value)
            return value;
        return default;
    }

    private static string FormatArgValue(object? value)
    {
        return value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            char c => $"'{c}'",
            _ => value.ToString() ?? "null"
        };
    }

    #region Legacy API for backwards compatibility

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
                if (!namespaceGroups.TryGetValue(test.Namespace, out var nsDesc))
                {
                    nsDesc = new TestNamespaceDescriptor { Namespace = test.Namespace };
                    namespaceGroups[test.Namespace] = nsDesc;
                }

                var classKey = $"{test.Namespace}.{test.ClassName}";
                var testClass = nsDesc.Classes.FirstOrDefault(c => c.FullyQualifiedName == classKey);

                if (testClass == null)
                {
                    testClass = new TestClassDescriptor
                    {
                        Namespace = test.Namespace,
                        ClassName = test.ClassName,
                        FullyQualifiedName = classKey
                    };
                    nsDesc.Classes.Add(testClass);
                }

                var existing = testClass.Methods.FirstOrDefault(m => m.MethodName == test.MethodName);
                if (existing != null)
                {
                    // Multiple test cases for same method - update count
                    var updated = new TestDescriptor
                    {
                        Namespace = existing.Namespace,
                        ClassName = existing.ClassName,
                        MethodName = existing.MethodName,
                        FullyQualifiedName = existing.FullyQualifiedName,
                        Framework = existing.Framework,
                        Type = TestType.Theory,
                        TestCaseCount = existing.TestCaseCount + 1,
                        DisplayName = existing.DisplayName,
                        SkipReason = existing.SkipReason,
                        Traits = existing.Traits
                    };
                    testClass.Methods.Remove(existing);
                    testClass.Methods.Add(updated);
                }
                else
                {
                    testClass.Methods.Add(new TestDescriptor
                    {
                        Namespace = test.Namespace,
                        ClassName = test.ClassName,
                        MethodName = test.MethodName,
                        FullyQualifiedName = test.FullyQualifiedName,
                        Framework = test.Framework,
                        Type = test.TestType,
                        TestCaseCount = 1,
                        DisplayName = test.DisplayName,
                        SkipReason = test.SkipReason,
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

    #endregion
}
