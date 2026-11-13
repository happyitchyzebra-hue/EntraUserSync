using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Configuration;
using EntraUserSync.Core.Udf;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EntraUserSync.Core.Registry;

public sealed class DattoUdfWriter(IRegistry registry, ILogger<DattoUdfWriter>? logger = null) : IUdfWriter
{
    public const string CentraStageKey = @"SOFTWARE\CentraStage";

    public UdfWriteResult Write(
        AppSettings settings,
        IReadOnlyDictionary<int, string?> valuesByUdf,
        bool whatIf)
    {
        try
        {
            // TODO: Confirm which registry view the Datto RMM agent reads (Registry64 vs Registry32).
            using var key = whatIf
                ? registry.OpenSubKey(RegistryHive.LocalMachine, CentraStageKey, writable: false, settings.RegistryView)
                : registry.CreateSubKey(RegistryHive.LocalMachine, CentraStageKey, settings.RegistryView);

            if (key is null)
            {
                return new UdfWriteResult
                {
                    Success = false,
                    Error = $"Could not open HKLM\\{CentraStageKey} (view {settings.RegistryView})."
                };
            }

            var changes = new List<UdfWriteChange>();

            foreach (var (udfNumber, raw) in valuesByUdf.OrderBy(kv => kv.Key))
            {
                var valueName = $"Custom{udfNumber}";
                var current = key.GetValue(valueName) as string;
                var incoming = ValueNormalizer.Normalize(raw, settings.MaxFieldLength);
                var action = UdfChangeLogic.Decide(current, incoming, settings.ClearFieldWhenEmpty);

                switch (action)
                {
                    case UdfAction.Unchanged:
                    case UdfAction.SkipEmpty:
                        changes.Add(new UdfWriteChange
                        {
                            UdfNumber = udfNumber,
                            PreviousValue = current,
                            NewValue = incoming,
                            Action = action.ToString()
                        });
                        break;

                    case UdfAction.Write:
                        changes.Add(new UdfWriteChange
                        {
                            UdfNumber = udfNumber,
                            PreviousValue = current,
                            NewValue = incoming,
                            Action = whatIf ? "WouldWrite" : "Write"
                        });
                        if (!whatIf)
                        {
                            key.SetValue(valueName, incoming!, RegistryValueKind.String);
                            logger?.LogInformation("Wrote {ValueName} = {Value}", valueName, incoming);
                        }
                        else
                        {
                            logger?.LogInformation("WHATIF: would write {ValueName} = {Value}", valueName, incoming);
                        }
                        break;

                    case UdfAction.Clear:
                        changes.Add(new UdfWriteChange
                        {
                            UdfNumber = udfNumber,
                            PreviousValue = current,
                            NewValue = string.Empty,
                            Action = whatIf ? "WouldClear" : "Clear"
                        });
                        if (!whatIf)
                        {
                            key.SetValue(valueName, string.Empty, RegistryValueKind.String);
                            logger?.LogInformation("Cleared {ValueName}", valueName);
                        }
                        else
                        {
                            logger?.LogInformation("WHATIF: would clear {ValueName}", valueName);
                        }
                        break;
                }
            }

            return new UdfWriteResult { Success = true, Changes = changes };
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed writing Datto UDF values");
            return new UdfWriteResult { Success = false, Error = ex.Message };
        }
    }
}
