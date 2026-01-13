# Asynkron.Profiler
Dotnet global tool CLI that wraps `dotnet-trace`/`dotnet-gcdump` to profile a command. It runs `dotnet-trace collect --output <file> -- <command>` and then parses `.nettrace`/`.etlx` to render CPU, memory allocations, exceptions, contention, or heap summaries into `profile-output/`. All logic currently lives in `src/ProfileTool/Program.cs` (no shared library yet).
