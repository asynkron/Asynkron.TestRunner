# TestTree
Test grouping utility in `src/Asynkron.TestRunner/TestTree.cs` that builds a hierarchy from test names. It splits names on dots for namespaces/classes and further splits the final segment on underscores (e.g. `Method_WhenCondition_ThenResult`) so method word groups are separate nodes. Related to [file://asynkron-testrunner.md](Asynkron.TestRunner).
