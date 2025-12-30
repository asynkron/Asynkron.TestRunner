# Asynkron.TestRunner

A .NET global tool that wraps `dotnet test`, captures TRX results, tracks test history, and displays regression charts.

## Features

- Wraps `dotnet test` and streams output in real-time
- Automatically captures TRX test results
- Tracks test history per project and command signature
- Shows pass/fail bar charts across runs
- Detects regressions (tests that passed before but now fail)
- Detects fixes (tests that failed before but now pass)
- Supports color and non-color (CI/piped) output modes

## Installation

```bash
dotnet tool install -g Asynkron.TestRunner
```

## Usage

### Run tests

```bash
testrunner -- dotnet test ./tests/MyTests
testrunner -- dotnet test --filter "Category=Unit"
```

### View history

```bash
testrunner stats -- dotnet test ./tests/MyTests
testrunner stats --history 5 -- dotnet test ./tests/MyTests
```

### View regressions

```bash
testrunner regressions -- dotnet test ./tests/MyTests
```

### Clear history

```bash
testrunner clear
```

## Example Output

### After a test run with regressions:

```
FAILED
──────────────────────────────────────────────────
  Passed:  132
  Failed:  4
  Skipped: 0
  Total:   136
  Duration: 2.3s
  Pass Rate: 97.1%

Regressions (3):
  ✗ MyTests.SomeTest.ThatUsedToPass
  ✗ MyTests.AnotherTest.NowFailing
  ✗ MyTests.ThirdTest.Broken
```

### History chart:

```
Test History (4 runs)
──────────────────────────────────────────────────────────────────────
2025-12-30 10:04  █████████████████████████████X  135/136 (99.3%)  ✗1
2025-12-30 10:03  █████████████████████████████X  132/136 (97.1%)  ✗4
2025-12-30 10:00  █████████████████████████████X  135/136 (99.3%)  ✗1
2025-12-30 10:00  █████████████████████████████X  135/136 (99.3%)  ✗1
```

## How History is Tracked

History is stored in `.testrunner/` and is tracked separately by:

- **Project** - hash of git repo root (or current directory)
- **Command signature** - hash of the test command (path + filters)

This means:
- Different repos have separate histories
- `--filter A` and `--filter B` have separate histories
- Running the same command compares correctly

## License

MIT
