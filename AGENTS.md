# Agent Guide

This repo is a .NET global tool that orchestrates worker processes to run tests and track history. The canonical architecture notes live in `.agents/`.

## How-to Docs
- `.agents/how-to-architecture.md`
- `.agents/how-to-run-test-workers.md`
- `.agents/how-to-stdio-protocol.md`

## Build and Test
- `dotnet build`
- `dotnet test tests/Asynkron.TestRunner.Tests`

## Key Paths
- `src/Asynkron.TestRunner/` – CLI coordinator and live UI.
- `src/Asynkron.TestRunner.Worker/` – worker process.
- `src/Asynkron.TestRunner.Protocol/` – stdio protocol.
- `.testrunner/` – run history, `resume.jsonl`, and `summary.md`.
