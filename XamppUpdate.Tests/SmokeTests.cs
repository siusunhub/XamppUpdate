using XamppUpdate.Models;
using Xunit;

namespace XamppUpdate.Tests;

public class SmokeTests
{
    [Fact]
    public void AppAssemblyIsReferenced()
    {
        Assert.Equal("Failed", UpdateStep.Failed.ToString());
    }
}
