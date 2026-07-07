using System.Text;

namespace DatfileCreator.Core;

/// <summary>
/// One completed dat held in memory for the preview window. Re-rendering with
/// a different structure option needs no re-hashing — the writers run again
/// over the same tree and hash data.
/// </summary>
public sealed class PreviewEntry
{
    public required string DatName { get; init; }
    public required string HeaderDate { get; init; }
    /// <summary>Full tree (per_root/per_top) or shallow node (per_all).</summary>
    public required FolderNode Node { get; init; }
    public required DatData Data { get; init; }
    /// <summary>Settings snapshot at the time of creation.</summary>
    public required DatSettings Settings { get; init; }
    /// <summary>True for the recursive (tree) generation modes.</summary>
    public required bool IsTree { get; init; }
}

public static class PreviewRenderer
{
    /// <summary>
    /// Re-render a completed dat to a string using a different structure
    /// option — same writers as the real dat writer, targeting a string.
    /// For per_all entries the node has no subdirs, so all structure options
    /// produce equivalent flat output (correct behaviour).
    /// </summary>
    public static string Render(PreviewEntry entry, string structureOverride)
    {
        var s = entry.Settings.Clone();
        s.Structure = structureOverride;
        var sb = new StringBuilder(64 * 1024);
        using var writer = new StringWriter(sb);
        DatWriter.WriteDatHeader(writer, entry.DatName, s, entry.HeaderDate);
        DatWriter.WriteBody(writer, entry.Node, entry.Data, s);
        writer.Write("</datafile>\n");
        return sb.ToString();
    }
}
