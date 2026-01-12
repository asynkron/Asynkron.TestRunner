# Flaky Test Detection
Prefer history-based flaky detection by analyzing run history (`history.json`) for pass/fail toggles across runs, instead of retrying within a run. Integrates with run summaries written via [file://run-summaries.md](Run Summaries) and stored by [file://result-store.md](ResultStore).
