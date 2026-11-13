using EntraUserSync.Core.Configuration;
using EntraUserSync.Core.Registry;
using Microsoft.Win32;

namespace EntraUserSync.Core.Tests;

public class DattoUdfWriterTests
{
    [Fact]
    public void Write_OnlyWhenChanged_AndRespectsClearFlag()
    {
        var registry = new FakeRegistry();
        registry.Set(
            RegistryHive.LocalMachine,
            DattoUdfWriter.CentraStageKey,
            RegistryView.Registry64,
            new Dictionary<string, object>
            {
                ["Custom2"] = "OldDept",
                ["Custom3"] = "Title"
            });

        var writer = new DattoUdfWriter(registry);
        var settings = new AppSettings
        {
            ClearFieldWhenEmpty = false,
            MaxFieldLength = 255,
            RegistryView = RegistryView.Registry64,
            FieldMappings =
            [
                new FieldMapping { AttributePath = "department", UdfNumber = 2 },
                new FieldMapping { AttributePath = "jobTitle", UdfNumber = 3 }
            ]
        };

        var result = writer.Write(settings, new Dictionary<int, string?>
        {
            [2] = "NewDept",
            [3] = "Title",
            [4] = null
        }, whatIf: false);

        Assert.True(result.Success);
        Assert.Contains(result.Changes, c => c.UdfNumber == 2 && c.Action == "Write");
        Assert.Contains(result.Changes, c => c.UdfNumber == 3 && c.Action == "Unchanged");
        Assert.Contains(result.Changes, c => c.UdfNumber == 4 && c.Action == "SkipEmpty");

        using var key = registry.OpenSubKey(RegistryHive.LocalMachine, DattoUdfWriter.CentraStageKey, false, RegistryView.Registry64)!;
        Assert.Equal("NewDept", key.GetValue("Custom2"));
        Assert.Equal("Title", key.GetValue("Custom3"));
    }

    [Fact]
    public void Write_WhatIf_DoesNotMutate()
    {
        var registry = new FakeRegistry();
        registry.Set(
            RegistryHive.LocalMachine,
            DattoUdfWriter.CentraStageKey,
            RegistryView.Registry64,
            new Dictionary<string, object> { ["Custom2"] = "Old" });

        var writer = new DattoUdfWriter(registry);
        var settings = new AppSettings { RegistryView = RegistryView.Registry64 };
        var result = writer.Write(settings, new Dictionary<int, string?> { [2] = "New" }, whatIf: true);
        Assert.True(result.Success);
        Assert.Contains(result.Changes, c => c.Action == "WouldWrite");
        using var key = registry.OpenSubKey(RegistryHive.LocalMachine, DattoUdfWriter.CentraStageKey, false, RegistryView.Registry64)!;
        Assert.Equal("Old", key.GetValue("Custom2"));
    }
}
