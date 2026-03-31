using Asynkron.TestRunner.Models;
using Spectre.Console;

namespace Asynkron.TestRunner;

public class TestTreeNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public List<TestTreeNode> Children { get; } = [];
    public List<string> Tests { get; } = []; // Leaf tests at this node
    public int TotalTestCount { get; set; }
}

public class TestTree
{
    // Use dot/underscore separators for namespace.class.method and BDD-style method names
    private static readonly char[] NameSeparators = ['.', '_'];

    private readonly TestTreeNode _root = new() { Name = "Tests", FullPath = "" };

    public TestTreeNode Root => _root;

    public void AddTests(IEnumerable<string> testNames)
    {
        foreach (var testName in testNames)
        {
            AddTest(testName);
        }

        // Calculate total counts
        CalculateTotalCounts(_root);
    }

    /// <summary>
    /// Adds tests from structured descriptors
    /// </summary>
    public void AddTestsFromDescriptors(IEnumerable<TestAssemblyDescriptor> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var ns in assembly.Namespaces)
            {
                foreach (var cls in ns.Classes)
                {
                    foreach (var method in cls.Methods)
                    {
                        // Use the fully qualified name (namespace.class.method)
                        AddTest(method.FullyQualifiedName);
                    }
                }
            }
        }

        // Calculate total counts
        CalculateTotalCounts(_root);
    }

    private void AddTest(string testName)
    {
        // Strip parameters: "Namespace.Class.Method(param1, param2)" -> "Namespace.Class.Method"
        var baseName = GetTestBaseName(testName);
        var parts = baseName.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);

        var current = _root;
        var pathSoFar = "";

        foreach (var part in parts)
        {
            pathSoFar = string.IsNullOrEmpty(pathSoFar) ? part : $"{pathSoFar}.{part}";

            var child = current.Children.FirstOrDefault(c => c.Name == part);
            if (child == null)
            {
                child = new TestTreeNode { Name = part, FullPath = pathSoFar };
                current.Children.Add(child);
            }
            current = child;
        }

        // Add the full test name as a leaf
        current.Tests.Add(testName);
    }

    private static string GetTestBaseName(string testName)
    {
        var parenIndex = testName.IndexOf('(');
        return parenIndex > 0 ? testName[..parenIndex] : testName;
    }

    private static int CalculateTotalCounts(TestTreeNode node)
    {
        var count = node.Tests.Count;
        foreach (var child in node.Children)
        {
            count += CalculateTotalCounts(child);
        }
        node.TotalTestCount = count;
        return count;
    }

    public void Render(int maxDepth = 5)
    {
        var tree = new Tree($"[bold]{_root.Name}[/] ({_root.TotalTestCount} tests)");

        foreach (var child in _root.Children.OrderBy(c => c.Name))
        {
            AddNodeToTree(tree, child, 1, maxDepth);
        }

        AnsiConsole.Write(tree);
    }

    private static void AddNodeToTree(IHasTreeNodes parent, TestTreeNode node, int depth, int maxDepth)
    {
        var label = node.Children.Count > 0 || node.Tests.Count > 1
            ? $"[blue]{node.Name}[/] ({node.TotalTestCount})"
            : $"[green]{node.Name}[/]";

        var treeNode = parent.AddNode(label);

        if (depth >= maxDepth)
        {
            if (node.Children.Count > 0)
            {
                treeNode.AddNode($"[dim]... {node.Children.Count} more groups[/]");
            }
            return;
        }

        foreach (var child in node.Children.OrderBy(c => c.Name))
        {
            AddNodeToTree(treeNode, child, depth + 1, maxDepth);
        }
    }

    /// <summary>
    /// Gets all nodes at a specific depth for running in groups
    /// </summary>
    public IEnumerable<TestTreeNode> GetNodesAtDepth(int depth)
    {
        return GetNodesAtDepthRecursive(_root, 0, depth);
    }

    private static IEnumerable<TestTreeNode> GetNodesAtDepthRecursive(TestTreeNode node, int currentDepth, int targetDepth)
    {
        if (currentDepth == targetDepth)
        {
            yield return node;
            yield break;
        }

        foreach (var child in node.Children)
        {
            foreach (var descendant in GetNodesAtDepthRecursive(child, currentDepth + 1, targetDepth))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Gets all test names under a node
    /// </summary>
    public static List<string> GetAllTests(TestTreeNode node)
    {
        var tests = new List<string>(node.Tests);
        foreach (var child in node.Children)
        {
            tests.AddRange(GetAllTests(child));
        }
        return tests;
    }

    /// <summary>
    /// Finds a node by its full path (e.g., "Namespace.Class.Method")
    /// </summary>
    public TestTreeNode? FindNodeByPath(string path)
    {
        return FindNodeByPath(_root, path);
    }

    /// <summary>
    /// Finds a node in the tree by its full path.
    /// </summary>
    public static TestTreeNode? FindNodeByPath(TestTreeNode root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return root;
        }

        var parts = path.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        foreach (var part in parts)
        {
            var child = current.Children.FirstOrDefault(c =>
                c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (child == null)
            {
                // Try partial match - path might be truncated
                child = current.Children.FirstOrDefault(c =>
                    c.FullPath.Contains(part, StringComparison.OrdinalIgnoreCase));
            }
            if (child == null)
            {
                return null;
            }

            current = child;
        }

        return current;
    }
}
