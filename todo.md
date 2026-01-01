We are aiming to improve on this testrunner. the idea is to orachestrate Dotnet Test.
dotnet test suffers from not being able to terminate freezing tests, it just hangs, or you have to run with hang blame which kills on the first hang.

The problem is how to get a set of tests "just the right size" to run in a single go, and still be able to pinpoint each of them with a single filter.

Therefore, we build a tree of test prefixes. leafnodes are single tests, and each parent node is a common prefix of its children.
We can then find entire branches with a given set of children. group those branches into a single test run.

Currently we don´t handle recursive runs or pinpointing failures within a group, that is the task here now.

* when dotnet test fails due to timeout, we get notified which test timed out, and we should be able to reason which test branches were before, or after that test. 
any branch (small or large) whose tests fully pass or fail can be removed from the set of tests to run.

eventually, there will be only single hanging tests left. which we can present to the user.

--- document your findings and improvements here ---

