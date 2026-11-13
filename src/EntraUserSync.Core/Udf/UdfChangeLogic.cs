namespace EntraUserSync.Core.Udf;

public enum UdfAction
{
    Unchanged,
    Write,
    Clear,
    SkipEmpty
}

public static class UdfChangeLogic
{
    /// <summary>
    /// Decide whether to write, clear, skip, or leave unchanged.
    /// <paramref name="incoming"/> should already be normalised (null means empty).
    /// </summary>
    public static UdfAction Decide(string? currentValue, string? incoming, bool clearWhenEmpty)
    {
        if (incoming is null)
        {
            if (!clearWhenEmpty)
                return UdfAction.SkipEmpty;

            if (string.IsNullOrEmpty(currentValue))
                return UdfAction.Unchanged;

            return UdfAction.Clear;
        }

        if (ValueNormalizer.ValuesEqual(currentValue, incoming))
            return UdfAction.Unchanged;

        return UdfAction.Write;
    }
}
