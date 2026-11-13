using System.Text.Json;
using EntraUserSync.Core.Graph;

namespace EntraUserSync.Core.Tests;

public class AttributePathResolverTests
{
    private static JsonElement User() => JsonDocument.Parse("""
        {
          "id": "abc",
          "userPrincipalName": "user@contoso.com",
          "department": "IT",
          "jobTitle": "Engineer",
          "onPremisesExtensionAttributes": {
            "extensionAttribute1": "EXT1"
          },
          "businessPhones": ["111", "222"],
          "manager": {
            "displayName": "Boss",
            "mail": "boss@contoso.com"
          }
        }
        """).RootElement;

    [Theory]
    [InlineData("department", "IT")]
    [InlineData("jobTitle", "Engineer")]
    [InlineData("onPremisesExtensionAttributes.extensionAttribute1", "EXT1")]
    [InlineData("manager.displayName", "Boss")]
    [InlineData("missing.path", null)]
    public void GetValue_DottedPaths(string path, string? expected)
    {
        Assert.Equal(expected, AttributePathResolver.GetValue(User(), path));
    }

    [Fact]
    public void GetValue_JoinsArrays()
    {
        Assert.Equal("111; 222", AttributePathResolver.GetValue(User(), "businessPhones"));
    }

    [Fact]
    public void SelectAndExpand_Helpers()
    {
        var paths = new[] { "department", "jobTitle", "manager.displayName", "onPremisesExtensionAttributes.extensionAttribute1" };
        var select = AttributePathResolver.GetTopLevelSelectProperties(paths);
        Assert.Contains("department", select);
        Assert.Contains("onPremisesExtensionAttributes", select);
        Assert.Contains("id", select);
        Assert.DoesNotContain("manager", select);
        Assert.True(AttributePathResolver.NeedsManagerExpand(paths));
        Assert.False(AttributePathResolver.NeedsManagerExpand(["department"]));
    }
}
