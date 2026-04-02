using Asynkron.TestRunner;
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
        Assert.Equal("Namespace", tree.Root.Children[0].Name);
    }

    [Fact]
    public void AddTests_MultipleTests_GroupsByCommonPrefix()
    {
        var tree = new TestTree();
        tree.AddTests([
            "MyApp.Tests.UserServiceTests.Create_ValidUser_Succeeds",
            "MyApp.Tests.UserServiceTests.Create_InvalidUser_Fails",
            "MyApp.Tests.OrderServiceTests.Process_ValidOrder_Succeeds"
        ]);

        Assert.Equal(3, tree.Root.TotalTestCount);

        // Should have MyApp at root
        Assert.Single(tree.Root.Children);
        Assert.Equal("MyApp", tree.Root.Children[0].Name);
    }

    [Fact]
    public void AddTests_TestsWithParameters_StripsParameters()
    {
        var tree = new TestTree();
        tree.AddTests([
            "Namespace.Class.Method(param1: \"value\", param2: 123)",
            "Namespace.Class.Method(param1: \"other\")"
        ]);

        // Both tests should end up at the same leaf node since parameters are stripped
        var namespaceNode = tree.Root.Children[0];
        var classNode = namespaceNode.Children[0];
        var methodNode = classNode.Children[0];

        Assert.Equal(2, methodNode.Tests.Count);
    }

    [Fact]
    public void AddTests_TestsWithUnderscoreSeparators_SplitsCorrectly()
    {
        var tree = new TestTree();
        tree.AddTests(["Suite_SubSuite_TestCase"]);

        Assert.Single(tree.Root.Children);
        var suiteNode = tree.Root.Children[0];
        Assert.Equal("Suite", suiteNode.Name);

        Assert.Single(suiteNode.Children);
        var subSuiteNode = suiteNode.Children[0];
        Assert.Equal("SubSuite", subSuiteNode.Name);

        Assert.Single(subSuiteNode.Children);
        var testCaseNode = subSuiteNode.Children[0];
        Assert.Equal("TestCase", testCaseNode.Name);
    }

    [Fact]
    public void GetNodesAtDepth_ReturnsCorrectNodes()
    {
        var tree = new TestTree();
        tree.AddTests([
            "A.B.C.Test1",
            "A.B.D.Test2",
            "X.Y.Z.Test3"
        ]);

        // Depth 0 is root
        var depth1Nodes = tree.GetNodesAtDepth(1).ToList();
        Assert.Equal(2, depth1Nodes.Count); // A and X

        var depth2Nodes = tree.GetNodesAtDepth(2).ToList();
        Assert.Equal(2, depth2Nodes.Count); // B and Y
    }

    [Fact]
    public void GetAllTests_ReturnsAllTestsUnderNode()
    {
        var tree = new TestTree();
        var testNames = new[]
        {
            "Parent.Child1.Test1",
            "Parent.Child1.Test2",
            "Parent.Child2.Test3"
        };
        tree.AddTests(testNames);

        var allTests = TestTree.GetAllTests(tree.Root);

        Assert.Equal(3, allTests.Count);
        foreach (var test in testNames)
        {
            Assert.Contains(test, allTests);
        }
    }

    [Fact]
    public void Root_InitiallyEmpty_HasZeroTestCount()
    {
        var tree = new TestTree();

        Assert.Equal(0, tree.Root.TotalTestCount);
        Assert.Empty(tree.Root.Children);
        Assert.Empty(tree.Root.Tests);
    }

    [Fact]
    public void AddTests_EmptyCollection_DoesNothing()
    {
        var tree = new TestTree();
        tree.AddTests([]);

        Assert.Equal(0, tree.Root.TotalTestCount);
        Assert.Empty(tree.Root.Children);
    }

    [Fact]
    public void TotalTestCount_CalculatesCorrectlyAcrossDepths()
    {
        var tree = new TestTree();
        tree.AddTests([
            "A.B.Test1",
            "A.B.Test2",
            "A.C.Test3",
            "D.E.Test4"
        ]);

        Assert.Equal(4, tree.Root.TotalTestCount);

        // A should have 3 tests total
        var nodeA = tree.Root.Children.First(c => c.Name == "A");
        Assert.Equal(3, nodeA.TotalTestCount);

        // D should have 1 test total
        var nodeD = tree.Root.Children.First(c => c.Name == "D");
        Assert.Equal(1, nodeD.TotalTestCount);
    }

    [Fact]
    public void AddTests_DuplicateTests_AddsAllToLeaf()
    {
        var tree = new TestTree();
        tree.AddTests([
            "Namespace.Class.Method",
            "Namespace.Class.Method"
        ]);

        Assert.Equal(2, tree.Root.TotalTestCount);
    }

    [Fact]
    public void FullPath_BuildsCorrectlyForNestedNodes()
    {
        var tree = new TestTree();
        tree.AddTests(["A.B.C.Test"]);

        var nodeA = tree.Root.Children[0];
        var nodeB = nodeA.Children[0];
        var nodeC = nodeB.Children[0];

        Assert.Equal("A", nodeA.FullPath);
        Assert.Equal("A.B", nodeB.FullPath);
        Assert.Equal("A.B.C", nodeC.FullPath);
    }

    [Fact]
    public void FindNodeByPath_ReturnsCorrectNode()
    {
        var tree = new TestTree();
        tree.AddTests(["Namespace.Class.Method.Test1"]);

        var node = tree.FindNodeByPath("Namespace.Class");

        Assert.NotNull(node);
        Assert.Equal("Class", node.Name);
        Assert.Equal("Namespace.Class", node.FullPath);
    }

    [Fact]
    public void FindNodeByPath_ReturnsNull_WhenPathNotFound()
    {
        var tree = new TestTree();
        tree.AddTests(["Namespace.Class.Method.Test1"]);

        var node = tree.FindNodeByPath("NonExistent.Path");

        Assert.Null(node);
    }

    [Fact]
    public void FindNodeByPath_EmptyPath_ReturnsRoot()
    {
        var tree = new TestTree();
        tree.AddTests(["Namespace.Class.Method.Test1"]);

        var node = tree.FindNodeByPath("");

        Assert.NotNull(node);
        Assert.Same(tree.Root, node);
    }

    [Fact]
    public void FindNodeByPath_CaseInsensitive()
    {
        var tree = new TestTree();
        tree.AddTests(["Namespace.Class.Method.Test1"]);

        var node = tree.FindNodeByPath("namespace.class");

        Assert.NotNull(node);
        Assert.Equal("Class", node.Name);
    }

    [Fact]
    public void FindNodeByPath_FindsDeepNodes()
    {
        var tree = new TestTree();
        tree.AddTests([
            "A.B.C.D.E.F.Test1",
            "A.B.C.D.E.F.Test2"
        ]);

        var node = tree.FindNodeByPath("A.B.C.D.E.F");

        Assert.NotNull(node);
        Assert.Equal("F", node.Name);
        // Tests are stored at the deepest leaf nodes (Test1 and Test2 are children of F)
        Assert.Equal(2, node.TotalTestCount);
    }

    [Fact]
    public void FindNodeByPath_WorksWithUnderscoreSeparators()
    {
        var tree = new TestTree();
        tree.AddTests(["Namespace.Class.Method_WhenCondition_ThenResult"]);

        var node = tree.FindNodeByPath("Namespace.Class.Method");

        Assert.NotNull(node);
        Assert.Equal("Method", node.Name);
        Assert.Single(node.Children);
        Assert.Equal("WhenCondition", node.Children[0].Name);
    }
}
