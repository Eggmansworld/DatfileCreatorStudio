using Avalonia.Media;

namespace DatfileCreatorStudio;

/// <summary>
/// Embedded icon geometries (Material Design path data) so no icon font/package is required
/// </summary>
public static class Icons
{
    public static readonly StreamGeometry FolderOpen = StreamGeometry.Parse(
        "M19,20H4C2.89,20 2,19.1 2,18V6C2,4.89 2.89,4 4,4H10L12,6H19A2,2 0 0,1 21,8H21L4,8V18L6.14,10H23.21L20.93,18.5C20.7,19.37 19.92,20 19,20Z");

    public static readonly StreamGeometry Play = StreamGeometry.Parse(
        "M8,5.14V19.14L19,12.14L8,5.14Z");

    public static readonly StreamGeometry Stop = StreamGeometry.Parse(
        "M18,18H6V6H18V18Z");

    public static readonly StreamGeometry Close = StreamGeometry.Parse(
        "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z");

    public static readonly StreamGeometry ContentCopy = StreamGeometry.Parse(
        "M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z");

    public static readonly StreamGeometry ChevronUp = StreamGeometry.Parse(
        "M7.41,15.41L12,10.83L16.59,15.41L18,14L12,8L6,14L7.41,15.41Z");

    public static readonly StreamGeometry ChevronDown = StreamGeometry.Parse(
        "M7.41,8.58L12,13.17L16.59,8.58L18,10L12,16L6,10L7.41,8.58Z");

    public static readonly StreamGeometry Delete = StreamGeometry.Parse(
        "M19,4H15.5L14.5,3H9.5L8.5,4H5V6H19M6,19A2,2 0 0,0 8,21H16A2,2 0 0,0 18,19V7H6V19Z");

    public static readonly StreamGeometry ContentSave = StreamGeometry.Parse(
        "M15,9H5V5H15M12,19A3,3 0 0,1 9,16A3,3 0 0,1 12,13A3,3 0 0,1 15,16A3,3 0 0,1 12,19M17,3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V7L17,3Z");
}
