using Asynkron.Profiler;

namespace Asynkron.TestRunner.Profiling;

public sealed class WorkerProfileAnalyzer
{
    private readonly ProfilerTraceAnalyzer _analyzer;

    public WorkerProfileAnalyzer(ResultStore resultStore)
        : this(Path.Combine(resultStore.StoreFolder, "profiles"))
    {
    }

    public WorkerProfileAnalyzer(string outputDirectory)
    {
        _analyzer = new ProfilerTraceAnalyzer(outputDirectory);
    }

    public string OutputDirectory => _analyzer.OutputDirectory;

    public CpuProfileResult AnalyzeCpuTrace(string traceFile) => _analyzer.AnalyzeCpuTrace(traceFile);

    public CpuProfileResult AnalyzeSpeedscope(string speedscopePath) => _analyzer.AnalyzeSpeedscope(speedscopePath);

    public AllocationCallTreeResult AnalyzeAllocationTrace(string traceFile) => _analyzer.AnalyzeAllocationTrace(traceFile);

    public ExceptionProfileResult AnalyzeExceptionTrace(string traceFile) => _analyzer.AnalyzeExceptionTrace(traceFile);

    public ContentionProfileResult AnalyzeContentionTrace(string traceFile) => _analyzer.AnalyzeContentionTrace(traceFile);
}
