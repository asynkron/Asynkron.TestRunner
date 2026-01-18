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
    // Use dot and underscore separators for namespace.class.method or namespace_class_method structures
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
        var pathParts = new List<string>();

        foreach (var part in parts)
        {
            pathParts.Add(part);
            
            // Reconstruct path from the original base name to preserve separators
            var pathSoFar = ReconstructPath(baseName, pathParts);

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

    private static string ReconstructPath(string originalName, List<string> parts)
    {
        // Reconstruct the path by finding each part in the original name
        // and preserving the separator character(s) between them
        var result = parts[0];
        var currentPos = originalName.IndexOf(parts[0]);
        
        for (int i = 1; i < parts.Count; i++)
        {
            var nextPart = parts[i];
            // Note: In rare cases where a part name appears multiple times,
            // this may find the wrong occurrence, but this is acceptable for
            // typical test naming patterns
            var nextPos = originalName.IndexOf(nextPart, currentPos + parts[i - 1].Length);
            
            if (nextPos >= 0)
            {
                // Extract the separator between the parts
                var separator = originalName.Substring(currentPos + parts[i - 1].Length, nextPos - (currentPos + parts[i - 1].Length));
                result += separator + nextPart;
                currentPos = nextPos;
            }
            else
            {
                // Fallback to dot separator if we can't find the part
                result += "." + nextPart;
            }
        }
        
        return result;
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
