namespace DatfileCreator.Core;

/// <summary>
/// Extension include/exclude filters (Mixed mode only). Ported from the
/// Python suite's parse_ext_list / file_matches_filter.
/// </summary>
public static class ExtensionFilter
{
    /// <summary>
    /// Parse a comma-separated extension list into a set of lowercase
    /// extensions with leading dots. Accepts ".ima, .mfm" or "ima, mfm" or
    /// mixed. Full filenames (e.g. "thumbs.db") are kept verbatim so they can
    /// match exact-file excludes. Empty set means "no filter".
    /// </summary>
    public static HashSet<string> Parse(string? raw)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (string part in raw.Split(','))
        {
            string p = part.Trim().ToLowerInvariant();
            if (p.Length == 0)
                continue;
            // If it has a path separator, just keep the basename
            int slash = p.Replace('\\', '/').LastIndexOf('/');
            if (slash >= 0)
                p = p[(slash + 1)..];
            // Ensure leading dot on pure-extension entries ("ima" -> ".ima");
            // full filenames ("thumbs.db") stay as-is for exact matching
            if (!p.Contains('.'))
                p = "." + p;
            if (p.Length > 0)
                result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// Test whether a filename passes the include/exclude filters.
    /// Case-insensitive on both extension and full basename. A filter entry
    /// matches if it starts with '.' and equals the file's extension, or if
    /// it equals the file's full basename (Thumbs.db-style entries).
    /// </summary>
    public static bool Matches(string filename, HashSet<string> include, HashSet<string> exclude)
    {
        string baseName = Path.GetFileName(filename).ToLowerInvariant();
        string ext = Path.GetExtension(baseName); // includes leading dot, "" if none

        static bool MatchesAny(HashSet<string> filter, string baseName, string ext)
        {
            foreach (string entry in filter)
            {
                if (entry == baseName)
                    return true;
                if (entry.StartsWith('.') && entry == ext)
                    return true;
            }
            return false;
        }

        if (include.Count > 0 && !MatchesAny(include, baseName, ext))
            return false;
        if (exclude.Count > 0 && MatchesAny(exclude, baseName, ext))
            return false;
        return true;
    }
}
