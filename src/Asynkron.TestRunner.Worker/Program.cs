using Asynkron.TestRunner.Protocol;
using Asynkron.TestRunner.Worker;

// Worker process: reads commands from stdin, writes events to stdout
var frameworks = new ITestFramework[]
{
    new XUnitFramework(),
    new NUnitFramework()
};

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Monitor parent process - exit if stdin closes (parent died)
_ = Task.Run(async () =>
{
    try
    {
        while (!cts.IsCancellationRequested)
        {
            // Peek at stdin - if parent died, this returns -1 immediately
            var peek = Console.In.Peek();
            if (peek == -1 && Console.IsInputRedirected)
            {
                // Parent died, exit immediately
                Environment.Exit(0);
            }
            await Task.Delay(500, cts.Token);
        }
    }
    catch { /* ignore */ }
});

await RunWorkerAsync(Console.In, Console.Out, frameworks, cts.Token);

static async Task RunWorkerAsync(
    TextReader input,
    TextWriter output,
    ITestFramework[] frameworks,
    CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var message = await ProtocolIO.ReadAsync(input, ct);
        if (message == null)
        {
            break;  // EOF
        }

        try
        {
            switch (message)
            {
                case DiscoverCommand discover:
                    await HandleDiscoverAsync(discover, frameworks, output);
                    break;

                case RunCommand run:
                    await HandleRunAsync(run, frameworks, output, ct);
                    break;

                case CancelCommand:
                    // Handled via CancellationToken
                    return;

                default:
                    ProtocolIO.Write(output, new ErrorEvent($"Unknown command: {message.GetType().Name}"));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            ProtocolIO.Write(output, new ErrorEvent(ex.Message, ex.StackTrace));
        }
    }
}

static async Task HandleDiscoverAsync(
    DiscoverCommand cmd,
    ITestFramework[] frameworks,
    TextWriter output)
{
    var framework = frameworks.FirstOrDefault(f => f.CanHandle(cmd.Assembly));
    if (framework == null)
    {
        ProtocolIO.Write(output, new ErrorEvent($"No framework found for assembly: {cmd.Assembly}"));
        return;
    }

    var tests = framework.Discover(cmd.Assembly)
        .Select(t => new DiscoveredTestInfo(t.FullyQualifiedName, t.DisplayName, t.SkipReason))
        .ToList();

    ProtocolIO.Write(output, new DiscoveredEvent(tests));
}

static async Task HandleRunAsync(
    RunCommand cmd,
    ITestFramework[] frameworks,
    TextWriter output,
    CancellationToken ct)
{
    var framework = frameworks.FirstOrDefault(f => f.CanHandle(cmd.Assembly));
    if (framework == null)
    {
        ProtocolIO.Write(output, new ErrorEvent($"No framework found for assembly: {cmd.Assembly}"));
        return;
    }

    var passed = 0;
    var failed = 0;
    var skipped = 0;
    var startTime = DateTime.UtcNow;

    // Create timeout token if specified
    using var timeoutCts = cmd.TimeoutSeconds.HasValue
        ? new CancellationTokenSource(TimeSpan.FromSeconds(cmd.TimeoutSeconds.Value))
        : new CancellationTokenSource();

    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

    try
    {
        await foreach (var result in framework.RunAsync(cmd.Assembly, cmd.Tests, linkedCts.Token))
        {
            switch (result)
            {
                case TestStarted started:
                    ProtocolIO.Write(output, new TestStartedEvent(started.FullyQualifiedName, started.DisplayName));
                    break;

                case TestPassed testPassed:
                    passed++;
                    ProtocolIO.Write(output, new TestPassedEvent(
                        testPassed.FullyQualifiedName,
                        testPassed.DisplayName,
                        testPassed.Duration.TotalMilliseconds));
                    break;

                case TestFailed testFailed:
                    failed++;
                    ProtocolIO.Write(output, new TestFailedEvent(
                        testFailed.FullyQualifiedName,
                        testFailed.DisplayName,
                        testFailed.Duration.TotalMilliseconds,
                        testFailed.ErrorMessage,
                        testFailed.StackTrace));
                    break;

                case TestSkipped testSkipped:
                    skipped++;
                    ProtocolIO.Write(output, new TestSkippedEvent(
                        testSkipped.FullyQualifiedName,
                        testSkipped.DisplayName,
                        testSkipped.Reason));
                    break;

                case TestOutput testOutput:
                    ProtocolIO.Write(output, new TestOutputEvent(
                        testOutput.FullyQualifiedName,
                        testOutput.Text));
                    break;
            }
        }
    }
    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
    {
        ProtocolIO.Write(output, new ErrorEvent("Test run timed out"));
    }

    var duration = DateTime.UtcNow - startTime;
    ProtocolIO.Write(output, new RunCompletedEvent(passed, failed, skipped, duration.TotalMilliseconds));
}
