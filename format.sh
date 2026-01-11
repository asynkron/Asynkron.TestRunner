#!/usr/bin/env bash
set -e

dotnet format Asynkron.TestRunner.sln
roslynator fix Asynkron.TestRunner.sln
