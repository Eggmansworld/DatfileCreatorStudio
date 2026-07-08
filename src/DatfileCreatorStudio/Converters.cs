using Avalonia.Data.Converters;

namespace DatfileCreatorStudio;

public static class Converters
{
    /// <summary>
    /// Dims the main content while the log drawer is expanded, so the pushed-up
    /// panels recede and the focus lands on the drawer (and any layout overlap
    /// from the shrunken area reads as "backgrounded" rather than accidental).
    /// </summary>
    public static readonly FuncValueConverter<bool, double> ExpandedToDim =
        new(expanded => expanded ? 0.4 : 1.0);
}
