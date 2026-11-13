using System.Text;

namespace EntraUserSync.Core.Udf;

public static class ValueNormalizer
{
    /// <summary>
    /// Collapse CR/LF/tabs to spaces, trim, truncate to maxLength.
    /// Returns null when the source is null or whitespace (after normalisation).
    /// </summary>
    public static string? Normalize(string? value, int maxLength)
    {
        if (value is null)
            return null;

        var sb = new StringBuilder(value.Length);
        var previousWasSpace = false;

        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                if (!previousWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    previousWasSpace = true;
                }
                continue;
            }

            if (ch == ' ')
            {
                if (!previousWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    previousWasSpace = true;
                }
                continue;
            }

            sb.Append(ch);
            previousWasSpace = false;
        }

        var normalised = sb.ToString().Trim();
        if (normalised.Length == 0)
            return null;

        if (normalised.Length > maxLength)
            normalised = normalised[..maxLength];

        return normalised;
    }

    public static bool ValuesEqual(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);
}
