namespace Asynkron.TestRunner.SampleXunit;

public class SampleTests
{
    [Xunit.Fact]
    public void Passes()
    {
    }

    [Xunit.Fact]
    public void Fails()
    {
        throw new System.Exception("This test should fail");
    }
}

public class OtherTests
{
    [Xunit.Fact]
    public void AlsoPasses()
    {
    }
}

