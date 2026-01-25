# Release Process
[file://asynkron-testrunner.md](Asynkron.TestRunner) is released by pushing a git tag matching `v*` (for example `v0.9.2`).

GitHub Actions:
- `.github/workflows/pack.yml` runs on tag push `v*`, runs tests, packs with `Version="${GITHUB_REF_NAME#v}"`, and publishes to NuGet.
