namespace Asynkron.TestRunner.Profiling;

public sealed record WorkerProfilingSettings(
    bool Cpu,
    bool Memory,
    bool Latency,
    bool Exception)
{
    public bool Enabled => Cpu || Memory || Latency || Exception;

    public WorkerProfilingOptions CreateOptions(string outputDirectory, string label)
    {
        return new WorkerProfilingOptions(Cpu, Memory, Latency, Exception, outputDirectory, label);
    }
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
