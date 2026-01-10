# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Asynkron.TestRunner is a .NET global tool that wraps `dotnet test` with enhanced capabilities:
- TRX result capture and history tracking
- Regression detection across runs
- Automatic hang detection and test isolation
- Visual test hierarchy trees (via Spectre.Console)

## Build and Test Commands

```bash
# Build the solution
dotnet build

# Run all tests
dotnet test tests/Asynkron.TestRunner.Tests

# Run a specific test
dotnet test tests/Asynkron.TestRunner.Tests --filter "FullyQualifiedName~TestClassName"

# Install locally as a global tool (for testing the CLI)
dotnet pack src/Asynkron.TestRunner -o nupkg
dotnet tool install -g Asynkron.TestRunner --add-source ./nupkg --version <version>
```

## Architecture

### Core Components

- **Program.cs** - CLI entry point; parses commands (`stats`, `clear`, `list`, `isolate`, `regressions`) and options (`--timeout`, `--quiet`, `--parallel`)

- **TestRunner.cs** - Main test execution orchestrator. Wraps `dotnet test` with TRX logging and `--blame-hang` for timeout detection. On hang detection, automatically triggers isolation.

- **IsolateRunner.cs** - Binary-search isolation for hanging tests. Builds test batches from the tree, runs them with graduated timeouts, and recursively drills down into hanging batches until individual culprits are found. Supports parallel batch execution.

- **TestTree.cs** / **TestTreeNode** - Hierarchical representation of tests by namespace/class/method. Used for batching and visual rendering.

- **TestDiscovery.cs** - Reflection-based test discovery using `MetadataLoadContext`. Finds methods with test attributes (`[Test]`, `[Fact]`, `[Theory]`, etc.) without loading assemblies into the runtime.

- **ResultStore.cs** - Persists test run results in `.testrunner/` directory, keyed by project hash and command signature.

- **TrxParser.cs** - Parses Visual Studio TRX result files to extract pass/fail/timeout status.

- **ChartRenderer.cs** - Spectre.Console based rendering for history charts, regression diffs, and isolation summaries.

- **TimeoutStrategy.cs** - Configures per-test and batch timeout behavior (fixed or graduated).

### Models (`Models/` directory)

- **TestRunResult.cs** - Stores results of a test run (passed/failed/timed-out test names, timestamps)
- **TestDescriptor.cs** - Structured test metadata (namespace, class, method, framework, traits)

### Key Patterns

1. **vstest vs dotnet test mode**: IsolateRunner detects `.dll` paths in args to switch between `dotnet vstest` (uses `/testCaseFilter:`) and `dotnet test` (uses `--filter`).

2. **Filter syntax**: Uses `FullyQualifiedName~<pattern>` for namespace.class.method matching. Multiple prefixes joined with `|` (OR).

3. **Batch isolation**: Tests are grouped into batches of max 2000 tests. Batches are run with process-level and idle timeouts. Hanging batches are recursively subdivided.

4. **Test tree navigation**: The tree is built from FQN paths split by `.`. Used to find "maximal under-the-limit" nodes for efficient batching.

## Testing

Tests use xUnit. Key test files:
- `IsolateRunnerTests.cs` - Tests for batch building and isolation logic
- `TestTreeTests.cs` - Tests for tree construction and traversal
- `TrxParserTests.cs` - Tests for TRX parsing

## Tool Usage

Once installed, the tool is invoked as `testrunner`:

```bash
testrunner                          # Run all tests
testrunner "MyClass"                # Filter by pattern
testrunner isolate "Slow"           # Isolate hanging tests
testrunner isolate -p 4 "Tests"     # Parallel isolation
testrunner stats                    # View run history
```
