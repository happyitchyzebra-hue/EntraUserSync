using EntraUserSync.Core.Configuration;

namespace EntraUserSync.Core.Tests;

public class ConfigValidatorTests
{
    private readonly ConfigValidator _validator = new();

    private static AppSettings ValidSettings() => new()
    {
        TenantId = "contoso.onmicrosoft.com",
        ClientId = "34d26ebe-da2c-4155-81f7-7b7776b78b07",
        CertificateThumbprint = "9BF693EFABFDD1261D566856F349BDB1AEC689FE",
        FieldMappings =
        [
            new FieldMapping { AttributePath = "department", UdfNumber = 2 },
            new FieldMapping { AttributePath = "jobTitle", UdfNumber = 3 }
        ]
    };

    [Fact]
    public void Validate_ValidSettings_Succeeds()
    {
        var result = _validator.Validate(ValidSettings());
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_MissingTenant_Fails()
    {
        var settings = ValidSettings();
        settings.TenantId = "";
        var result = _validator.Validate(settings);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("TenantId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_BadClientId_Fails()
    {
        var settings = ValidSettings();
        settings.ClientId = "not-a-guid";
        var result = _validator.Validate(settings);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_ShortThumbprint_Fails()
    {
        var settings = ValidSettings();
        settings.CertificateThumbprint = "ABCD";
        var result = _validator.Validate(settings);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_BadAllowedSuffix_Fails()
    {
        var settings = ValidSettings();
        settings.AllowedUpnSuffixes = ["user@contoso.com"];
        var result = _validator.Validate(settings);
        Assert.False(result.IsValid);
    }
}
