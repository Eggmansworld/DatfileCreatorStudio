namespace DatfileCreator.Core;

/// <summary>
/// All options for a dat generation run. Field meanings and defaults are
/// identical to the Python suite's Settings dataclass; string enums use the
/// same tokens ("mixed"/"zipped", "per_top"/"per_root"/"per_all",
/// "opt2"/"opt3") so saved configs stay interchangeable.
/// </summary>
public sealed class DatSettings
{
    // Paths
    public string InputRoot { get; set; } = "";
    public string OutputRoot { get; set; } = "";
    /// <summary>Optional prefix on all dat filenames.</summary>
    public string ParentName { get; set; } = "";

    // Header fields
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Runtime only — blank means "today". Not persisted.</summary>
    public string Date { get; set; } = "";
    public string Author { get; set; } = "";
    public string Url { get; set; } = "";
    public string Homepage { get; set; } = "";
    public string Comment { get; set; } = "";

    /// <summary>"mixed" | "zipped"</summary>
    public string DatType { get; set; } = "mixed";

    /// <summary>"per_top" | "per_root" | "per_all"</summary>
    public string GenMode { get; set; } = "per_root";

    /// <summary>
    /// "opt2" ("Standard") | "opt3" ("Grouped"). Retired: "opt1" ("Dirs"),
    /// which wrote rom entries inside dir tags that RomVault never reads, and
    /// "opt4" ("Grouped + Folders"), whose extra folder entries RomVault
    /// discarded — it produced the same result as "opt3".
    /// </summary>
    public string Structure { get; set; } = "opt2";

    public bool UseMachine { get; set; }
    public bool InclGameDesc { get; set; } = true;

    // Mixed-only
    public bool ForcePacking { get; set; } = true;

    // Zipped-only
    public bool InclFileDate { get; set; }

    /// <summary>7-Zip-ZStandard path (used by tools ported in later sessions).</summary>
    public string SevenZipPath { get; set; } = @"C:\Program Files\7-Zip-Zstandard\7z.exe";

    // Incremental update (engine support lands in Session 3)
    public bool Incremental { get; set; }
    public string IncrementalDatPath { get; set; } = "";
    public bool RetireOldDats { get; set; } = true;

    // Shared options
    public bool IncludeMd5 { get; set; }
    public bool IncludeSha256 { get; set; }
    public bool IncludeBlake3 { get; set; }

    /// <summary>Comma-separated include filter, Mixed only. Empty = include everything.</summary>
    public string ExtInclude { get; set; } = "";
    /// <summary>Comma-separated exclude filter (extensions or exact filenames), Mixed only.</summary>
    public string ExtExclude { get; set; } = "";

    public bool Multithread { get; set; } = true;
    public int Threads { get; set; } = 4;

    /// <summary>0 = auto (85% of NIC speed); &gt;0 = manual Mbit/s cap. Engine support lands in Session 2.</summary>
    public int NetCapMbps { get; set; }

    public DatSettings Clone() => (DatSettings)MemberwiseClone();

    public bool IsMixed => DatType == "mixed";
}
