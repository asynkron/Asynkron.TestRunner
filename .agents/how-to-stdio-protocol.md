# Stdio Protocol

The coordinator and worker communicate using JSON lines over stdin/stdout. Each line is a single JSON object with a `type` discriminator.

Protocol implementation lives in:
- `src/Asynkron.TestRunner.Protocol/Messages.cs`

## Commands (Coordinator → Worker)
- `discover` – list tests in an assembly.
- `run` – run all tests or a provided list of FQNs with an optional timeout.
- `cancel` – stop the current operation.

## Events (Worker → Coordinator)
- `discovered` – returns `DiscoveredTestInfo` list.
- `started` – test started.
- `passed` – test passed (duration).
- `failed` – test failed (duration, error, stack).
- `skipped` – test skipped.
- `output` – test stdout/stderr.
- `completed` – run finished summary.
- `error` – worker error.

## Message Fields
- `FullyQualifiedName` and `DisplayName` are included for test events.
- `run` command can specify `Tests` (list of FQNs) and `TimeoutSeconds`.

## Framing Rules
- One JSON object per line.
- Worker stops after emitting `completed` or `error`.

## Serialization Helpers
- `ProtocolIO.Serialize` and `ProtocolIO.ReadAsync` handle JSON lines.
