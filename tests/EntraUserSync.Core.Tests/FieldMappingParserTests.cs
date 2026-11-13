using EntraUserSync.Core.Configuration;

namespace EntraUserSync.Core.Tests;

public class FieldMappingParserTests
{
    [Fact]
    public void Parse_DefaultStyleMappings()
    {
        var mappings = FieldMappingParser.Parse("department=2;jobTitle=3");
        Assert.Equal(2, mappings.Count);
        Assert.Equal("department", mappings[0].AttributePath);
        Assert.Equal(2, mappings[0].UdfNumber);
        Assert.Equal("jobTitle", mappings[1].AttributePath);
        Assert.Equal(3, mappings[1].UdfNumber);
    }

    [Fact]
    public void Parse_DottedManagerPath()
    {
        var mappings = FieldMappingParser.Parse("manager.displayName=5");
        Assert.Single(mappings);
        Assert.Equal("manager.displayName", mappings[0].AttributePath);
    }

    [Theory]
    [InlineData("department=0")]
    [InlineData("department=31")]
    [InlineData("=2")]
    [InlineData("department")]
    [InlineData("department=abc")]
    public void Parse_Invalid_Throws(string raw)
    {
        Assert.Throws<FormatException>(() => FieldMappingParser.Parse(raw));
    }

    [Fact]
    public void Parse_DuplicateUdf_Throws()
    {
        Assert.Throws<FormatException>(() => FieldMappingParser.Parse("department=2;jobTitle=2"));
    }

    [Theory]
    [InlineData("9BF693EFABFDD1261D566856F349BDB1AEC689FE", "9BF693EFABFDD1261D566856F349BDB1AEC689FE")]
    [InlineData("9b:f6:93:ef", "9BF693EF")]
    [InlineData("  ab cd  ", "ABCD")]
    public void SanitizeThumbprint_StripsNonHex(string input, string expected)
    {
        Assert.Equal(expected, FieldMappingParser.SanitizeThumbprint(input));
    }
}
