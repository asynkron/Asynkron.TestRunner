# VSTest Hang Detection & Isolation

Complete guide to using the test runner with incomplete JS engine implementations (or any test suite with hanging tests).

## Overview

The test runner now supports `dotnet vstest` with intelligent hang detection and recursive isolation. When tests hang, it automatically:

1. Detects the hang via `--blame-hang` timeout
2. Identifies which specific test(s) hung from TRX results
3. Isolates the hanging test(s)
4. Splits remaining tests into smaller batches
5. Recursively continues until all non-hanging tests complete

## Quick Start

### Run Test262 Suite with Hang Detection

```bash
cd ~/git/asynkron/Asynkron.JsEngine

# Run with 5-second timeout per test, isolate hangs
testrunner isolate --timeout 5 -- dotnet vstest \
  tests/Asynkron.JsEngine.Tests.Test262/bin/Release/net10.0/Asynkron.JsEngine.Tests.Test262.dll

# With parallel execution (4-way)
testrunner isolate --timeout 5 --parallel 4 -- dotnet vstest \
  tests/Asynkron.JsEngine.Tests.Test262/bin/Release/net10.0/Asynkron.JsEngine.Tests.Test262.dll
```

### How It Works

#### Phase 1: Discovery
```
Discovering tests...
Found 92,945 test cases across 1,631 methods

Test hierarchy:
Tests (92,945 tests)
└── Tests
    └── GeneratedTests
        ├── Array_from (2 test cases)
        ├── Expressions_class_dstr (3,840 test cases)
        └── ...
```

#### Phase 2: Batching
Tests are grouped by method name (1,631 batches):
- Small batches: 2-6 test cases
- Large batches: up to 3,840 test cases
- Average: ~57 test cases per batch

#### Phase 3: Execution with Clean Progress

```
[1/1631] ► Tests.GeneratedTests.Array_from (2 tests)
[1/1631] ✓ Tests.GeneratedTests.Array_from (2 passed)

[2/1631] ► Tests.GeneratedTests.Expressions_class_dstr (3840 tests)
[2/1631] ⏱ Tests.GeneratedTests.Expressions_class_dstr (42 hung, 3798 passed) - isolating...
```

**No framework logs!** Just clean tree-based progress.

#### Phase 4: Recursive Isolation

When a batch hangs:

```
Drilling into hanging batch: Tests.GeneratedTests.Expressions_class_dstr
  Running 2 child batches...
    ► Tests.GeneratedTests.Expressions_class_dstr (1920 tests)
    ✓ Tests.GeneratedTests.Expressions_class_dstr (1920 passed)
    ⏱ Tests.GeneratedTests.Expressions_class_dstr (1 hung, 1919 passed) - isolating...
      ⏱ ISOLATED: Tests.GeneratedTests.Expressions_class_dstr("language/expressions/class/dstr/async-gen-meth-ary-ptrn-elem-id-init-fn-name-arrow.js",False)
```

The runner continues recursively until:
- All non-hanging tests pass ✓
- All hanging tests are isolated ⏱
- Any failures are reported ✗

## Test262 Structure

### Files Generated

- **test262-structure.json** (881 KB) - Complete test metadata
- **test262-summary.md** - Statistics and overview
- **test262-top-methods.json** - Largest test methods

### Statistics

| Metric | Value |
|--------|-------|
| Total Methods | 1,631 |
| Total Test Cases | 92,945 |
| Avg Cases/Method | ~57 |
| Largest Method | 3,840 cases |
| Smallest Method | 2 cases |

### Top 10 Heaviest Methods

1. `Expressions_class_dstr` - 3,840 cases
2. `Statements_class_dstr` - 3,840 cases
3. `Statements_forAwaitOf` - 2,431 cases
4. `Object_defineProperty` - 2,250 cases
5. `Statements_class_elements` - 2,088 cases
6. `Expressions_class_elements` - 1,881 cases
7. `Object_defineProperties` - 1,264 cases
8. `Expressions_object_dstr` - 1,122 cases
9. `Statements_forOf_dstr` - 1,095 cases
10. `RegExp` - 976 cases

## Command Reference

### Isolate Mode Options

```bash
testrunner isolate [options] -- [test command]
```

**Options:**
- `--timeout <seconds>` - Per-test timeout (default: 30s)
- `--parallel <n>` - Run batches in parallel (default: 1)
- `--filter <pattern>` - Filter tests by name

**Examples:**

```bash
# Sequential with 10s timeout
testrunner isolate --timeout 10 -- dotnet vstest Tests.dll

# 8-way parallel with 5s timeout
testrunner isolate --timeout 5 --parallel 8 -- dotnet vstest Tests.dll

# Filter specific tests
testrunner isolate --timeout 5 --filter "Array" -- dotnet vstest Tests.dll
```

### Quiet Mode (Simple Run)

```bash
# Just run tests, show tree before execution
testrunner --quiet -- dotnet vstest Tests.dll

# With timeout (no isolation, just basic hang detection)
testrunner --timeout 20 -- dotnet vstest Tests.dll
```

## Output Symbols

| Symbol | Meaning |
|--------|---------|
| `►` | Batch starting |
| `✓` | Batch passed (all tests passed) |
| `✗` | Batch failed (assertion failures) |
| `⏱` | Batch hung (timeout detected) |

## Implementation Details

### VSTest vs DotNet Test

The runner automatically detects mode by checking for `.dll` files in args:

**VSTest Mode:**
- Uses `--testCaseFilter` for filtering
- Uses `--ResultsDirectory` for output
- Skips `--no-build` (not supported)
- Example: `dotnet vstest Tests.dll`

**DotNet Test Mode:**
- Uses `--filter` for filtering
- Uses `--results-directory` for output
- Adds `--no-build` flag
- Example: `dotnet test MySolution.sln`

### Hang Detection

Uses `--blame-hang --blame-hang-timeout <seconds>`:

1. **TRX Results** - Shows which tests passed/failed/timed out
2. **Blame-Hang Artifacts** - `Sequence_*.xml` files indicate hangs
3. **Process Guard** - Kills batch if no output for too long

### Batch Splitting Strategy

When a batch of N tests hangs:

1. Parse TRX to get passed tests (P tests)
2. Parse blame-hang to get hanging tests (H tests)
3. Remaining tests: R = N - P - H
4. Split R into 2 equal batches
5. Run each batch recursively
6. Continue until all tests isolated or passed

### Tree Structure

Tests are organized by:
- **Namespace** (e.g., `Asynkron.JsEngine.Tests`)
- **Class** (e.g., `AdditionalArrayMethodsTests`)
- **Method** (e.g., `Array_From_UsesIterator`)

Batches can be created at any level:
- All tests in a namespace (large batch)
- All tests in a class (medium batch)
- All test cases in a method (small batch)
- Individual test case (single test)

## Troubleshooting

### No tests discovered

**Problem:** `Found 0 tests`

**Solution:** Ensure DLLs are built:
```bash
dotnet build --configuration Release
```

### All batches timing out

**Problem:** Timeout too short for setup

**Solution:** Increase timeout:
```bash
testrunner isolate --timeout 30 -- dotnet vstest Tests.dll
```

### Memory issues with large suites

**Problem:** 92K tests consume too much memory

**Solution:** Use filtering to run subsets:
```bash
# Run only Array tests
testrunner isolate --filter "Array" -- dotnet vstest Tests.dll

# Run only specific method
testrunner isolate --filter "Array_from" -- dotnet vstest Tests.dll
```

### Parallel execution hangs

**Problem:** Deadlocks with parallel test execution

**Solution:** Reduce parallelism:
```bash
# Try 2-way instead of 8-way
testrunner isolate --parallel 2 -- dotnet vstest Tests.dll
```

## Next Steps

1. **Run Test262 suite** - Identify all hanging tests in your JS engine
2. **Export results** - Save isolated hanging tests for tracking
3. **Fix hangs** - Use isolated test names to debug specific issues
4. **Re-run** - Verify fixes didn't introduce new hangs

## Example Session

```bash
# Discover and save test structure
testrunner --quiet -- dotnet vstest Test262.dll > /dev/null

# Run with hang detection
testrunner isolate --timeout 5 --parallel 4 -- dotnet vstest Test262.dll

# Output:
# Found 92,945 tests across 1,631 methods
#
# [1/1631] ✓ Tests.GeneratedTests.Array_from (2 passed)
# [2/1631] ⏱ Tests.GeneratedTests.Array_prototype (145 hung, 890 passed)
#   Drilling into hanging batch...
#     ⏱ ISOLATED: Array_prototype("test1.js",False)
#     ⏱ ISOLATED: Array_prototype("test2.js",True)
#     ✓ Remaining tests passed (888 passed)
# ...
#
# Results:
#   Total: 92,945 tests
#   Passed: 85,234
#   Hung: 7,711 (isolated)
#   Failed: 0
```

Perfect for iterative development of incomplete implementations!
