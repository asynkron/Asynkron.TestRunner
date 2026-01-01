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

- [x] Fix the file structure for dotnet projects, /src /tests etc.
  - Created `Asynkron.TestRunner.sln` at root
  - Moved source files to `src/Asynkron.TestRunner/`
  - Created `tests/Asynkron.TestRunner.Tests/` with xUnit test project
  - Updated csproj to reference README from root with `..\..\README.md`
  - Added `InternalsVisibleTo` for test project access

- [x] Add tests for the testrunner itself
  - Added 30 unit tests covering:
    - `TestTreeTests`: 13 tests for test tree hierarchy, path building, node traversal
    - `TrxParserTests`: 10 tests for TRX file parsing, timeout detection, result aggregation
    - `TestRunResultTests`: 7 tests for pass rate, regressions, fixes detection
  - All tests pass

- [x] Set up continuous integration for the testrunner project, github actions build publish release on nuget
  - Updated `.github/workflows/ci.yml` to use solution file and run tests
  - Updated `.github/workflows/pack.yml` to run tests before publishing and use new project path

- [x] Implement recursive test runs to isolate hanging tests
  - Added `DrillDownHangingBatchAsync` method in `IsolateRunner.cs` that recursively drills into hanging batches
  - Added `FindNodeByPath` to `TestTree.cs` for navigating to specific tree nodes by full path
  - Added `BuildChildBatches` to create batches from child nodes of a hanging batch
  - Added tracking via `_isolatedHangingTests` list and `_completedBatches` set to avoid re-running
  - Algorithm: When a batch hangs, the driller:
    1. Finds the tree node(s) corresponding to the hanging batch
    2. Creates child batches from the node's children
    3. Runs each child batch:
       - If it passes, the branch is clean and skipped
       - If it hangs with a single test, that test is isolated
       - If it hangs with multiple tests, recursively drill deeper
    4. Eventually isolates individual hanging tests
  - Added `MaxRecursionDepth = 10` to prevent infinite recursion
  - Added 7 new unit tests for `FindNodeByPath` in `TestTreeTests.cs`
  - All 36 tests pass
- [x] Improve logging and reporting of test results
  - Added `RenderIsolationSummary` to `ChartRenderer.cs` for clean isolation summary output
  - Added `RenderDrillProgress` to show progress during recursive drilling with status icons
  - Added `ExportSummary` to write test results to a file
  - Added `IsolationResult` record type for returning detailed isolation results
  - Added `RunWithResultAsync` method that returns `IsolationResult` with:
    - List of isolated hanging tests
    - List of failed batches
    - Total/passed batch counts
    - Total duration
  - Added `IsolatedHangingTests` and `FailedBatches` public properties on `IsolateRunner`
- [x] Explore timeout strategies for individual tests
  - Created `TimeoutStrategy.cs` with 4 timeout modes:
    - **Fixed**: Constant timeout for all tests (default: 20s)
    - **Adaptive**: Timeout based on historical test durations (median * 3x multiplier)
    - **Graduated**: Timeout that doubles on retries (10s → 20s → 40s)
    - **None**: No timeout (tests run until completion)
  - Added `TimeoutMode` enum and `TimeoutStrategy` class with:
    - `GetTimeout(attemptNumber)`: Get timeout for a specific attempt
    - `GetBatchTimeout(testCount, attemptNumber)`: Calculate batch timeout based on test count
    - `FromOptions(mode, timeoutSeconds, store)`: Parse from CLI options
    - `GetDescription()`: Human-readable description
  - Integrated with `IsolateRunner`:
    - Updated constructor to accept `TimeoutStrategy` (backward compatible with int)
    - Uses `GetBatchTimeout()` for batch timeouts
    - Increments attempt counter during recursive drilling for graduated timeouts
    - Shows timeout description in output
  - Added 14 new unit tests in `TimeoutStrategyTests.cs`
  - All 49 tests pass
- [x] Consider parallel execution of non-hanging test groups
  - Added `_maxParallelBatches` field to `IsolateRunner` to control concurrency
  - Added `maxParallelBatches` parameter to constructors (default: 1 for sequential)
  - Implemented `RunBatchesInParallelAsync` using `SemaphoreSlim` for throttling
  - Added thread-safe output handling with locks for console writes
  - Parallel mode runs independent batches concurrently:
    - Uses `Task.WhenAll` to await all batch tasks
    - Results are collected in order and printed as a summary after completion
    - Progress is shown with completion count and status icons (✓/⏱/✗)
  - Added `--parallel/-p` CLI option for `isolate` command:
    - `testrunner isolate -p 4` runs up to 4 batches concurrently
    - `testrunner isolate -p` defaults to CPU count
  - Added 8 new unit tests in `IsolateRunnerTests.cs`
  - All 57 tests pass
- [x] Document the testrunner usage and configuration options
  - Updated README.md with:
    - Parallel isolation section (`--parallel/-p` option)
    - Configuration Options table (timeout, isolate options)
    - Filter pattern examples
  - Help text in Program.cs already comprehensive
- [x] Ensure we can merge TRX results from multiple runs effectively
  - Enhanced `TrxParser.MergeResults()` method to properly merge multiple TRX results:
    - Deduplicates test names (case-insensitive)
    - Handles test retries: if a test passes in any run, it's counted as passed
    - Priority: Passed > Failed > TimedOut
    - Takes earliest timestamp and longest duration
  - Added 9 new unit tests for merge scenarios:
    - Deduplication, passed overrides failed/timed out on retry
    - Failed overrides timed out, case-insensitivity
    - Duration and timestamp handling
  - All 66 tests pass
