using Xunit;

namespace Asynkron.TestRunner.Tests;

public class WorkerStdoutIsolationTests
{
    [Fact]
    public async Task Worker_discovery_survives_stdout_noise_from_test_assembly()
    {
        var repoRoot = FindRepoRoot();
        var workerDll = FindBuiltFile(repoRoot, "src/Asynkron.TestRunner.Worker", "testrunner-worker.dll");
        var sampleDll = FindBuiltFile(repoRoot, "tests/Asynkron.TestRunner.SampleXunit", "Asynkron.TestRunner.SampleXunit.dll");

        await using var worker = WorkerProcess.Spawn(workerPath: workerDll);

        var tests = await worker.DiscoverAsync(sampleDll);

        Assert.Contains(tests, test => test.FullyQualifiedName == "Asynkron.TestRunner.SampleXunit.SampleTests.Passes");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Asynkron.TestRunner.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new DirectoryNotFoundException("Could not locate repo root (Asynkron.TestRunner.sln).");
        }

        return dir.FullName;
    }

    private static string FindBuiltFile(string repoRoot, string projectDir, string fileName)
    {
        var fullProjectDir = Path.Combine(repoRoot, projectDir);
        var candidateFrameworks = new[] { "net10.0", "net9.0", "net8.0" };
        var candidateConfigs = new[] { "Debug", "Release" };

        foreach (var config in candidateConfigs)
        foreach (var tfm in candidateFrameworks)
        {
            var path = Path.Combine(fullProjectDir, "bin", config, tfm, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"Could not find built output '{fileName}' under '{fullProjectDir}/bin'.");
    }
}
