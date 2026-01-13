# Agent Guide


## Brain and memory
This is the most important instruction you MUST follow.
Read `.agents/memory/README.md` NOW for full details.
And ALWAYS use the memory tools when working or searching for information on this repository.
And ALWAYS save important information to memory.

This repo is a .NET global tool that orchestrates worker processes to run tests and track history. The canonical architecture notes live in `.agents/`.

## How-to Docs
- `.agents/how-to-architecture.md`
- `.agents/how-to-run-test-workers.md`
- `.agents/how-to-stdio-protocol.md`

## Build and Test
- `dotnet build`
- `dotnet test tests/Asynkron.TestRunner.Tests`

## Defaults
- You can fix build warnings, update documentation, and update memory without asking first.
- Use judgment calls like a senior engineer: fix small issues you notice, clean up after your own changes, and run relevant tests without asking.

## Key Paths
- `src/Asynkron.TestRunner/` – CLI coordinator and live UI.
- `src/Asynkron.TestRunner.Worker/` – worker process.
- `src/Asynkron.TestRunner.Protocol/` – stdio protocol.
- `.testrunner/` – run history, `resume.jsonl`, and `summary.md`.
