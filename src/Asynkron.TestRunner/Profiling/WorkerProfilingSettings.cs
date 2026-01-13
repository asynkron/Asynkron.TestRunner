namespace Asynkron.TestRunner.Profiling;

public sealed record WorkerProfilingSettings(
    bool Cpu,
    bool Memory,
    bool Latency,
    bool Exception,
    string? RootFilter)
{
    public bool Enabled => Cpu || Memory || Latency || Exception;

    public WorkerProfilingOptions CreateOptions(string outputDirectory, string label)
    {
        return new WorkerProfilingOptions(Cpu, Memory, Latency, Exception, outputDirectory, label);
    }

    public string? NormalizedRootFilter => string.IsNullOrWhiteSpace(RootFilter) ? null : RootFilter;
}

public sealed record WorkerProfilingOptions(
    bool Cpu,
    bool Memory,
    bool Latency,
    bool Exception,
    string OutputDirectory,
    string Label)
{
    public bool Enabled => Cpu || Memory || Latency || Exception;
}
