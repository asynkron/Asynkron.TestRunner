# ResultStore
Persists test run history for [file://asynkron-testrunner.md](Asynkron.TestRunner). Saves run summaries under `.testrunner/`, supports recent run comparison to detect regressions and fixes.

Notes:
- Stores history per repo under `.testrunner/<projectHash>/<commandHash>/history.json`.
- Can locate the newest history file in a repo via `FindLatestHistoryFile()` (by history file write time).
- Can create a store bound to an existing history file via `ResultStore.FromHistoryFile(...)` so re-runs append to the same history file.
