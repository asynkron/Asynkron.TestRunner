# CLI Flags
`testrunner` supports subcommands: `run`, `list`, `tree`, `serve`, `mcp`, `stats`, `regressions`, `clear`, plus `-h/--help`.

Common options and defaults (Program.cs, TestRunner.cs):
- `--filter/-f`: no filter by default (runs/lists all tests).
- `--timeout/-t`: 30s per test when omitted.
- `--hang-timeout`: defaults to the test timeout if not set (falls back to `--timeout`, or 30s if that is omitted).
- `--workers/-w`: defaults to 4; supplying the flag without a number uses `Environment.ProcessorCount`.
- `--quiet/-q`: switches to minimal/quiet output (no live UI); default is interactive UI.
- `--console/-c`: streaming console mode (plain text, resilient); default is interactive UI.
- `--verbose/-v`: writes worker diagnostics to stderr; off by default.
- `--log <file>`: writes diagnostics to file; off by default. With flag but no value, uses `testrunner.log`.
- `--resume [file]`: resume disabled by default; `--resume` with no path uses `.testrunner/resume.jsonl`.
- `--port` (serve/mcp): defaults to 5123.
- `--output/-o` (tree): writes to stdout when omitted.
- `--history/-n` (stats): defaults to 10 entries.
