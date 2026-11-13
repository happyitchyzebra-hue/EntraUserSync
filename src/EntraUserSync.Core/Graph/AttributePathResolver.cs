using System.Text.Json;

namespace EntraUserSync.Core.Graph;

public static class AttributePathResolver
{
    public static string? GetValue(JsonElement root, string dottedPath)
    {
        if (string.IsNullOrWhiteSpace(dottedPath))
            return null;

        var current = root;
        var segments = dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out var next))
                return null;

            current = next;
        }

        return FormatElement(current);
    }

    public static string? FormatElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join("; ",
            element.EnumerateArray()
                .Select(FormatElement)
                .Where(v => !string.IsNullOrWhiteSpace(v))),
        JsonValueKind.Object => element.GetRawText(),
        _ => element.ToString()
    };

    public static IReadOnlyList<string> GetTopLevelSelectProperties(IEnumerable<string> attributePaths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "id",
            "userPrincipalName"
        };

        foreach (var path in attributePaths)
        {
            var top = path.Split('.', 2)[0].Trim();
            if (string.IsNullOrWhiteSpace(top))
                continue;
            if (top.Equals("manager", StringComparison.OrdinalIgnoreCase))
                continue;
            set.Add(top);
        }

        return set.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool NeedsManagerExpand(IEnumerable<string> attributePaths) =>
        attributePaths.Any(p => p.StartsWith("manager.", StringComparison.OrdinalIgnoreCase) ||
                                p.Equals("manager", StringComparison.OrdinalIgnoreCase));
}
