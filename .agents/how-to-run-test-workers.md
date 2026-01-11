# How to Run Test Workers

## Coordinator Workflow
The coordinator spawns worker processes via `WorkerProcess.Spawn()` and communicates over stdio.

- Worker executable: `testrunner-worker.dll`
- Spawn command: `dotnet testrunner-worker.dll`
- Protocol: JSON lines over stdin/stdout

Key file: `src/Asynkron.TestRunner/WorkerProcess.cs`

## Running via CLI
Preferred flow is to use the CLI; it handles discovery, batching, and retries.

```bash
testrunner run <assembly.dll> [--filter pattern] [--timeout N] [--workers N]
```

## Development Worker Path
When running locally in debug, the coordinator looks for:

- `src/Asynkron.TestRunner.Worker/bin/Debug/net10.0/testrunner-worker.dll`

If needed, you can pass an explicit worker path to `WorkerProcess.Spawn(workerPath)`.

## Lifecycle Summary
1. Spawn worker process (`dotnet testrunner-worker.dll`).
2. Send `discover` to list tests.
3. Send `run` with optional test list and timeout.
4. Read events until `completed` or `error`.
5. Dispose or kill worker if needed.

Related docs: `./how-to-stdio-protocol.md`.
