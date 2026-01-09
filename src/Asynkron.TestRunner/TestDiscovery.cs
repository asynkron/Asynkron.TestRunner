using System.Diagnostics;
using Asynkron.TestRunner.Models;

namespace Asynkron.TestRunner;

/// <summary>
/// Discovers tests in assemblies using dotnet vstest
/// </summary>
public class TestDiscovery
{
    /// <summary>
    /// Discovers all tests in the given assembly paths using vstest
    /// </summary>
    public static async Task<List<TestAssemblyDescriptor>> DiscoverTestsAsync(IEnumerable<string> assemblyPaths)
    {
        var results = new List<TestAssemblyDescriptor>();

        foreach (var path in assemblyPaths)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"Warning: Assembly not found: {path}");
                continue;
            }

            try
            {
                var descriptor = await DiscoverTestsInAssemblyAsync(path);
                if (descriptor != null)
                {
                    results.Add(descriptor);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error discovering tests in {path}: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// Discovers tests in a single assembly using vstest --listTests
    /// </summary>
    public static async Task<TestAssemblyDescriptor?> DiscoverTestsInAssemblyAsync(string assemblyPath)
    {
        // Run dotnet vstest --listTests
        var output = await RunVsTestListAsync(assemblyPath);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        // Parse test names from output
        var testNames = ParseTestNames(output);
        if (testNames.Count == 0)
        {
            return null;
        }

        // Group tests by namespace -> class -> method
        var namespaceGroups = new Dictionary<string, TestNamespaceDescriptor>();

        foreach (var testName in testNames)
        {
            var descriptor = ParseTestName(testName);
            if (descriptor == null)
                continue;

            // Get or create namespace
            if (!namespaceGroups.TryGetValue(descriptor.Namespace, out var namespaceDesc))
            {
                namespaceDesc = new TestNamespaceDescriptor { Namespace = descriptor.Namespace };
                namespaceGroups[descriptor.Namespace] = namespaceDesc;
            }

            // Get or create class
            var classKey = $"{descriptor.Namespace}.{descriptor.ClassName}";
            var testClass = namespaceDesc.Classes.FirstOrDefault(c => c.FullyQualifiedName == classKey);

            if (testClass == null)
            {
                testClass = new TestClassDescriptor
                {
                    Namespace = descriptor.Namespace,
                    ClassName = descriptor.ClassName,
                    FullyQualifiedName = classKey
                };
                namespaceDesc.Classes.Add(testClass);
            }

            // Check if we already have this method (multiple test cases = theory)
            var existingMethod = testClass.Methods.FirstOrDefault(m => m.MethodName == descriptor.MethodName);
            if (existingMethod != null)
            {
                // Increment test case count for theories/parameterized tests
                var updatedMethod = new TestDescriptor
                {
                    Namespace = existingMethod.Namespace,
                    ClassName = existingMethod.ClassName,
                    MethodName = existingMethod.MethodName,
                    FullyQualifiedName = existingMethod.FullyQualifiedName,
                    Framework = existingMethod.Framework,
                    Type = TestType.Theory, // Multiple test cases means it's a theory
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
                testClass.Methods.Add(descriptor);
            }
        }

        return new TestAssemblyDescriptor
        {
            AssemblyPath = assemblyPath,
            AssemblyName = Path.GetFileNameWithoutExtension(assemblyPath),
            Namespaces = namespaceGroups.Values.OrderBy(n => n.Namespace).ToList()
        };
    }

    private static async Task<string> RunVsTestListAsync(string assemblyPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("vstest");
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("--listTests");

        using var process = new Process { StartInfo = startInfo };
        var output = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) output.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException($"Test discovery timed out for {assemblyPath}");
        }

        return output.ToString();
    }

    private static List<string> ParseTestNames(string output)
    {
        var tests = new List<string>();
        var inTestList = false;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("The following Tests are available:"))
            {
                inTestList = true;
                continue;
            }
            if (inTestList && !string.IsNullOrWhiteSpace(trimmed))
            {
                // Skip non-test lines
                if (trimmed.StartsWith("Test run for") ||
                    trimmed.StartsWith("VSTest version"))
                {
                    continue;
                }
                tests.Add(trimmed);
            }
        }

        return tests;
    }

    private static TestDescriptor? ParseTestName(string fullTestName)
    {
        // Strip parameters if present: "Namespace.Class.Method(x: 1, y: 2)" -> "Namespace.Class.Method"
        var baseName = fullTestName;
        var parenIndex = fullTestName.IndexOf('(');
        if (parenIndex > 0)
        {
            baseName = fullTestName[..parenIndex];
        }

        // Split on dots to get parts
        var parts = baseName.Split('.');

        // Handle different formats
        string namespaceName, className, methodName;

        if (parts.Length >= 3)
        {
            // Standard format: Namespace.Class.Method
            methodName = parts[^1];
            className = parts[^2];
            namespaceName = string.Join('.', parts[..^2]);
        }
        else if (parts.Length == 2)
        {
            // Format: Class.Method (no namespace)
            methodName = parts[^1];
            className = parts[^2];
            namespaceName = "Tests";
        }
        else if (parts.Length == 1)
        {
            // Format: Method only (generated tests, display names)
            // Use the test name as both class and method
            methodName = parts[0];
            className = "GeneratedTests";
            namespaceName = "Tests";
        }
        else
        {
            // Invalid format
            return null;
        }

        return new TestDescriptor
        {
            Namespace = namespaceName,
            ClassName = className,
            MethodName = methodName,
            FullyQualifiedName = $"{namespaceName}.{className}.{methodName}",
            Framework = TestFramework.Unknown, // Could detect from assembly later
            Type = TestType.Fact,
            TestCaseCount = 1,
            DisplayName = fullTestName, // Keep original name as display name
            SkipReason = null,
            Traits = new Dictionary<string, string>()
        };
    }
}
