# Stdio Protocol
JSON Lines protocol between coordinator and worker in [file://asynkron-testrunner.md](Asynkron.TestRunner). Commands: `discover`, `run`, `cancel`. Events: `discovered`, `started`, `passed`, `failed`, `skipped`, `output`, `completed`, `error`. Serialized in `src/Asynkron.TestRunner.Protocol`.

## Operational Notes
- Worker stdout must be reserved for protocol messages; redirect `Console.Out` away from stdout to avoid corrupting the stream.
- Coordinator should tolerate occasional non-protocol stdout lines (ignore them) rather than treating them as EOF.
