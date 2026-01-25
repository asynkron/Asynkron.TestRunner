# History Re-runs
[file://asynkron-testrunner.md](Asynkron.TestRunner) can re-run subsets of tests from the latest recorded run by using history selection flags (see [file://cli-flags.md](CLI Flags)).

Use cases:
- Loop on failures: run full suite → fix a few → `--history-failing` → fix → repeat.
- Regression checks: `--history-passing` re-runs previously passing tests; any new failures are likely regressions.

Selection is based on the most recently modified `.testrunner/<projectHash>/*/history.json` in the current repo (git root).
