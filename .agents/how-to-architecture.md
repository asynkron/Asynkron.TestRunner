# Architecture Overview

## Purpose
Asynkron.TestRunner is a .NET global tool that wraps `dotnet test` with richer execution, isolation, and reporting. The coordinator process runs the CLI and orchestrates workers. Workers run tests and stream events back over a JSON-line stdio protocol.

## Core Flow
- `Program.cs` parses CLI arguments and dispatches commands.
- `TestRunner.cs` discovers tests, runs worker batches, manages retries, and renders live output.
- `WorkerProcess.cs` spawns the worker (`testrunner-worker.dll`) and handles stdio communication.
- `ResultStore.cs` persists run history under `.testrunner/`.
- `LiveDisplay.cs` renders live progress and queue metrics with Spectre.Console.
- `ResumeTracker.cs` maintains append-only `resume.jsonl` checkpoints.

## Key Directories
- `src/Asynkron.TestRunner/` – CLI coordinator, live UI, result store.
- `src/Asynkron.TestRunner.Worker/` – test execution worker.
- `src/Asynkron.TestRunner.Protocol/` – stdio protocol messages and serialization.
- `tests/` – unit tests (xUnit).

## Runtime Data
- `.testrunner/` – run history and summary markdown.
- `.testrunner/resume.jsonl` – append-only resume checkpoints.
- `.testrunner/summary.md` – full run summary (non-truncated lists).

## Relevant Files
- `src/Asynkron.TestRunner/Program.cs`
- `src/Asynkron.TestRunner/TestRunner.cs`
- `src/Asynkron.TestRunner/WorkerProcess.cs`
- `src/Asynkron.TestRunner/ResumeTracker.cs`
- `src/Asynkron.TestRunner.Protocol/Messages.cs`
