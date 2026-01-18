using System.Runtime.CompilerServices;

namespace Asynkron.TestRunner.SampleXunit;

public static class ModuleInit
{
    [ModuleInitializer]
    public static void Init()
    {
        // Intentionally writes to Console.Out to simulate real-world assemblies that emit stdout noise
        // during load, which can corrupt the worker JSON-lines protocol if stdout isn't isolated.
        Console.Out.WriteLine("SAMPLE_STDOUT_NOISE");
    }
}

