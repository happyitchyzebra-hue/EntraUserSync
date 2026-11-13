using System.Text.RegularExpressions;

namespace EntraUserSync.Core.Configuration;

public static partial class FieldMappingParser
{
    /// <summary>
    /// Parses registry/CLI format: department=2;jobTitle=3;manager.displayName=4
    /// </summary>
    public static IReadOnlyList<FieldMapping> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        var results = new List<FieldMapping>();
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1)
                throw new FormatException($"Invalid field mapping '{part}'. Expected attribute=N (N is 1-30).");

            var path = part[..eq].Trim();
            var numberText = part[(eq + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(path))
                throw new FormatException($"Invalid field mapping '{part}': attribute path is empty.");

            if (!int.TryParse(numberText, out var number) || number is < 1 or > 30)
                throw new FormatException($"Invalid UDF number in '{part}'. Must be an integer from 1 to 30.");

            results.Add(new FieldMapping { AttributePath = path, UdfNumber = number });
        }

        var duplicateUdf = results.GroupBy(m => m.UdfNumber).FirstOrDefault(g => g.Count() > 1);
        if (duplicateUdf is not null)
            throw new FormatException($"UDF number {duplicateUdf.Key} is mapped more than once.");

        return results;
    }

    public static string Format(IEnumerable<FieldMapping> mappings) =>
        string.Join(';', mappings.Select(m => $"{m.AttributePath}={m.UdfNumber}"));

    public static string SanitizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            return string.Empty;

        return NonHex().Replace(thumbprint, string.Empty).ToUpperInvariant();
    }

    [GeneratedRegex("[^0-9A-Fa-f]")]
    private static partial Regex NonHex();
}
