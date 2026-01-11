#!/usr/bin/env bash
set -e

dotnet format Asynkron.TestRunner.sln
roslynator fix Asynkron.TestRunner.sln --msbuild-path /usr/local/share/dotnet/sdk/9.0.304
