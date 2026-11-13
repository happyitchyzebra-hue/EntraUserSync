using EntraUserSync.Core.Udf;

namespace EntraUserSync.Core.Tests;

public class ValueNormalizerAndUdfLogicTests
{
    [Theory]
    [InlineData("  IT  ", 255, "IT")]
    [InlineData("A\r\nB\tC", 255, "A B C")]
    [InlineData("too-long-value", 3, "too")]
    [InlineData("   ", 255, null)]
    [InlineData(null, 255, null)]
    public void Normalize_CollapsesAndTruncates(string? input, int max, string? expected)
    {
        Assert.Equal(expected, ValueNormalizer.Normalize(input, max));
    }

    [Fact]
    public void Decide_WriteWhenChanged()
    {
        Assert.Equal(UdfAction.Write, UdfChangeLogic.Decide("Old", "New", clearWhenEmpty: false));
    }

    [Fact]
    public void Decide_UnchangedWhenEqual()
    {
        Assert.Equal(UdfAction.Unchanged, UdfChangeLogic.Decide("Same", "Same", clearWhenEmpty: false));
    }

    [Fact]
    public void Decide_SkipEmptyByDefault()
    {
        Assert.Equal(UdfAction.SkipEmpty, UdfChangeLogic.Decide("Old", null, clearWhenEmpty: false));
    }

    [Fact]
    public void Decide_ClearWhenConfigured()
    {
        Assert.Equal(UdfAction.Clear, UdfChangeLogic.Decide("Old", null, clearWhenEmpty: true));
    }

    [Fact]
    public void Decide_ClearNoOpWhenAlreadyEmpty()
    {
        Assert.Equal(UdfAction.Unchanged, UdfChangeLogic.Decide("", null, clearWhenEmpty: true));
        Assert.Equal(UdfAction.Unchanged, UdfChangeLogic.Decide(null, null, clearWhenEmpty: true));
    }
}
