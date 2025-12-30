<img src="assets/images/logo.png" width="100%" alt="Asynkron.TestRunner" />

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
- Default 20s per-test timeout (detects and reports hung tests)
- Easy filter syntax for running specific tests

## Installation

```bash
dotnet tool install -g Asynkron.TestRunner
```

## Usage

### Run all tests

```bash
testrunner                  # Runs: dotnet test
```

### Run filtered tests

```bash
testrunner "MyClass"        # Runs: dotnet test --filter "FullyQualifiedName~MyClass"
testrunner "Namespace.Test" # Matches any test containing that pattern
```

### Custom command

```bash
testrunner -- dotnet test ./tests/MyTests
testrunner -- dotnet test --filter "Category=Unit"
```

### List tests (without running)

```bash
testrunner list                 # List all tests
testrunner list "Storage"       # List tests matching 'Storage'
```

### Options

```bash
testrunner --timeout 60 "SlowTests"   # 60s hang timeout (default: 20s)
testrunner --timeout 0                 # Disable hang detection
testrunner --help                      # Show help
```

### View history

```bash
testrunner stats                                    # Default command history
testrunner stats -- dotnet test ./tests/MyTests     # Specific command history
testrunner stats --history 5                        # Last 5 runs
```

### View regressions

```bash
testrunner regressions      # Compare last 2 runs
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
