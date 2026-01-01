using Xunit;

namespace Asynkron.TestRunner.Tests;

public class TestTreeTests
{
    [Fact]
    public void AddTests_SingleTest_CreatesCorrectHierarchy()
    {
        var tree = new TestTree();
        tree.AddTests(["Namespace.Class.Method"]);

        Assert.Equal(1, tree.Root.TotalTestCount);
        Assert.Single(tree.Root.Children);

        var namespaceNode = tree.Root.Children[0];
        Assert.Equal("Namespace", namespaceNode.Name);
        Assert.Equal("Namespace", namespaceNode.FullPath);
    }

    [Fact]
    public void AddTests_MultipleTestsSameNamespace_GroupsCorrectly()
    {
        var tree = new TestTree();
        tree.AddTests([
            "Namespace.Class.Method1",
            "Namespace.Class.Method2",
            "Namespace.Class.Method3"
        ]);

        Assert.Equal(3, tree.Root.TotalTestCount);
        Assert.Single(tree.Root.Children);

        var namespaceNode = tree.Root.Children[0];
        Assert.Equal("Namespace", namespaceNode.Name);
        Assert.Equal(3, namespaceNode.TotalTestCount);
    }

    [Fact]
    public void AddTests_DifferentNamespaces_CreatesParallelBranches()
    {
        var tree = new TestTree();
        tree.AddTests([
            "Namespace1.Class.Method",
            "Namespace2.Class.Method"
        ]);

        Assert.Equal(2, tree.Root.TotalTestCount);
        Assert.Equal(2, tree.Root.Children.Count);
    }

    [Fact]
    public void AddTests_ParameterizedTest_StripsParameters()
    {
        var tree = new TestTree();
        tree.AddTests([
            "Namespace.Class.Method(param1, param2)",
            "Namespace.Class.Method(param3)"
        ]);

        Assert.Equal(2, tree.Root.TotalTestCount);

        // Navigate to Method node
        var node = tree.Root.Children[0].Children[0].Children[0];
        Assert.Equal("Method", node.Name);
        Assert.Equal(2, node.Tests.Count);
    }

    [Fact]
    public void AddTests_UnderscoreSeparator_SplitsCorrectly()
    {
        var tree = new TestTree();
        tree.AddTests(["Namespace_Class_Method"]);

        Assert.Equal(1, tree.Root.TotalTestCount);
        Assert.Equal(3, GetTreeDepth(tree.Root));
    }

    [Fact]
    public void GetAllTests_ReturnsAllTestsUnderNode()
    {
        var tree = new TestTree();
        tree.AddTests([
            "Namespace.Class1.Method1",
            "Namespace.Class1.Method2",
            "Namespace.Class2.Method1"
        ]);

        var allTests = TestTree.GetAllTests(tree.Root);
        Assert.Equal(3, allTests.Count);
    }

    [Fact]
    public void GetNodesAtDepth_ReturnsCorrectNodes()
    {
        var tree = new TestTree();
        tree.AddTests([
            "Namespace1.Class1.Method1",
            "Namespace1.Class2.Method2",
            "Namespace2.Class1.Method1"
        ]);

        // Depth 1 should return namespace nodes
        var depth1Nodes = tree.GetNodesAtDepth(1).ToList();
        Assert.Equal(2, depth1Nodes.Count);
        Assert.Contains(depth1Nodes, n => n.Name == "Namespace1");
        Assert.Contains(depth1Nodes, n => n.Name == "Namespace2");

        // Depth 2 should return class nodes
        var depth2Nodes = tree.GetNodesAtDepth(2).ToList();
        Assert.Equal(3, depth2Nodes.Count);
    }

    [Fact]
    public void AddTests_EmptyList_CreatesEmptyTree()
    {
        var tree = new TestTree();
        tree.AddTests([]);

        Assert.Equal(0, tree.Root.TotalTestCount);
        Assert.Empty(tree.Root.Children);
    }

    [Fact]
    public void AddTests_FullPath_BuildsCorrectly()
    {
        var tree = new TestTree();
        tree.AddTests(["A.B.C.D"]);

        var nodeA = tree.Root.Children[0];
        Assert.Equal("A", nodeA.FullPath);

        var nodeB = nodeA.Children[0];
        Assert.Equal("A.B", nodeB.FullPath);

        var nodeC = nodeB.Children[0];
        Assert.Equal("A.B.C", nodeC.FullPath);

        var nodeD = nodeC.Children[0];
        Assert.Equal("A.B.C.D", nodeD.FullPath);
    }

    private static int GetTreeDepth(TestTreeNode node)
    {
        if (node.Children.Count == 0)
            return 0;
        return 1 + node.Children.Max(GetTreeDepth);
    }
}
