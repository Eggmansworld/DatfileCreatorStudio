namespace DatfileCreator.Core;

/// <summary>
/// One folder in the input tree.
/// For Mixed: Items = full paths of all (filtered) files.
/// For Zipped: Items = full paths of .zip files only.
/// </summary>
public sealed class FolderNode
{
    public required string Name { get; set; }
    /// <summary>Relative path from the job root (empty for the root itself).</summary>
    public required string RelPath { get; init; }
    public List<string> Items { get; } = [];
    public List<FolderNode> Subdirs { get; } = [];
}

/// <summary>
/// Recursive input-tree scanning, ported from scan_tree_mixed / scan_tree_zipped.
/// Hidden/system items, dot-prefixed names, and symlinks/reparse points are
/// skipped exactly like the Python suite.
/// </summary>
public static class FolderScanner
{
    /// <summary>
    /// Matches the Python suite's is_hidden_or_system(): dot-prefixed basename
    /// on all platforms, plus Hidden/System attributes on Windows.
    /// </summary>
    public static bool IsHiddenOrSystem(FileSystemInfo info)
    {
        if (info.Name.StartsWith('.'))
            return true;
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSymlink(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enumerate a directory's children sorted by lowercase name (the Python
    /// suite's sorted(os.scandir(...), key=name.lower())). Returns an empty
    /// list when the directory cannot be read.
    /// </summary>
    public static List<FileSystemInfo> SortedChildren(string dirPath)
    {
        try
        {
            var infos = new DirectoryInfo(dirPath).GetFileSystemInfos();
            Array.Sort(infos, (a, b) =>
                string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant()));
            return [.. infos];
        }
        catch
        {
            return [];
        }
    }

    private static int ScanDepth(string rel) =>
        rel.Length == 0 ? 0 : rel.Replace('\\', '/').Split('/').Length;

    /// <summary>Recursively build a FolderNode tree for Mixed mode (all files).</summary>
    public static FolderNode ScanTreeMixed(
        string dirPath, string rel, CancellationToken stop,
        Action<string, int>? onScan,
        HashSet<string> extInclude, HashSet<string> extExclude)
    {
        var node = new FolderNode { Name = Path.GetFileName(dirPath), RelPath = rel };
        if (stop.IsCancellationRequested)
            return node;
        onScan?.Invoke(dirPath, ScanDepth(rel));

        foreach (var entry in SortedChildren(dirPath))
        {
            if (stop.IsCancellationRequested)
                break;
            if (IsHiddenOrSystem(entry) || IsSymlink(entry))
                continue;
            if (entry is FileInfo)
            {
                if (extInclude.Count > 0 || extExclude.Count > 0)
                {
                    if (!ExtensionFilter.Matches(entry.Name, extInclude, extExclude))
                        continue;
                }
                node.Items.Add(entry.FullName);
            }
            else if (entry is DirectoryInfo)
            {
                string childRel = rel.Length > 0 ? Path.Combine(rel, entry.Name) : entry.Name;
                node.Subdirs.Add(ScanTreeMixed(entry.FullName, childRel, stop, onScan,
                                               extInclude, extExclude));
            }
        }
        SortNode(node);
        return node;
    }

    /// <summary>Recursively build a FolderNode tree for Zipped mode (zip files only).</summary>
    public static FolderNode ScanTreeZipped(
        string dirPath, string rel, CancellationToken stop,
        Action<string, int>? onScan)
    {
        var node = new FolderNode { Name = Path.GetFileName(dirPath), RelPath = rel };
        if (stop.IsCancellationRequested)
            return node;
        onScan?.Invoke(dirPath, ScanDepth(rel));

        foreach (var entry in SortedChildren(dirPath))
        {
            if (stop.IsCancellationRequested)
                break;
            if (IsHiddenOrSystem(entry) || IsSymlink(entry))
                continue;
            if (entry is FileInfo && entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                node.Items.Add(entry.FullName);
            else if (entry is DirectoryInfo)
            {
                string childRel = rel.Length > 0 ? Path.Combine(rel, entry.Name) : entry.Name;
                node.Subdirs.Add(ScanTreeZipped(entry.FullName, childRel, stop, onScan));
            }
        }
        SortNode(node);
        return node;
    }

    private static void SortNode(FolderNode node)
    {
        node.Items.Sort((a, b) => string.CompareOrdinal(
            Path.GetFileName(a).ToLowerInvariant(), Path.GetFileName(b).ToLowerInvariant()));
        node.Subdirs.Sort((a, b) => string.CompareOrdinal(
            a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant()));
    }

    public static int CountItems(FolderNode node)
    {
        int total = node.Items.Count;
        foreach (var sub in node.Subdirs)
            total += CountItems(sub);
        return total;
    }

    public static List<string> CollectAllItems(FolderNode node)
    {
        var result = new List<string>(node.Items);
        foreach (var sub in node.Subdirs)
            result.AddRange(CollectAllItems(sub));
        return result;
    }
}
