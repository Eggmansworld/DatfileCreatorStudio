using System.Text.Json;
using DatfileCreator.Core;

// Parity harness: runs the DatfileCreator.Core engine with settings from a
// JSON file that uses the Python suite's field names, so the same file can
// drive both engines for byte-identical output comparison.
//
// Usage: ParityRunner --settings <file.json> [--preview-dir <dir>]
//   --preview-dir: additionally render every completed dat in all four
//   structure options via the preview renderer and write them there.

string? settingsPath = null;
string? previewDir = null;
string? analyzeDir = null;
string analyzeType = "zipped";
string? countDir = null;
string? validatePath = null;
string? bhuTarget = null;
string bhuDate = "";
bool bhuFp = false;
var bhuSet = new Dictionary<string, string?>();
string? mergeRoot = null;
string mergeDate = "2026-08-01";
string? packDir = null;
string packExts = "exe";
string? extractDir = null;
string sevenZip = @"C:\Program Files\7-Zip-Zstandard\7z.exe";
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--settings" && i + 1 < args.Length)
        settingsPath = args[++i];
    else if (args[i] == "--preview-dir" && i + 1 < args.Length)
        previewDir = args[++i];
    else if (args[i] == "--analyze" && i + 1 < args.Length)
        analyzeDir = args[++i];
    else if (args[i] == "--dat-type" && i + 1 < args.Length)
        analyzeType = args[++i];
    else if (args[i] == "--count" && i + 1 < args.Length)
        countDir = args[++i];
    else if (args[i] == "--validate" && i + 1 < args.Length)
        validatePath = args[++i];
    else if (args[i] == "--bhu" && i + 1 < args.Length)
        bhuTarget = args[++i];
    else if (args[i] == "--bhu-date" && i + 1 < args.Length)
        bhuDate = args[++i];
    else if (args[i] == "--bhu-set" && i + 1 < args.Length)
    {
        string[] kv = args[++i].Split('=', 2);
        bhuSet[kv[0]] = kv.Length > 1 ? kv[1] : "";
    }
    else if (args[i] == "--bhu-clear" && i + 1 < args.Length)
        bhuSet[args[++i]] = "";
    else if (args[i] == "--bhu-fp")
        bhuFp = true;
    else if (args[i] == "--merge" && i + 1 < args.Length)
        mergeRoot = args[++i];
    else if (args[i] == "--merge-date" && i + 1 < args.Length)
        mergeDate = args[++i];
    else if (args[i] == "--pack" && i + 1 < args.Length)
        packDir = args[++i];
    else if (args[i] == "--pack-exts" && i + 1 < args.Length)
        packExts = args[++i];
    else if (args[i] == "--extract" && i + 1 < args.Length)
        extractDir = args[++i];
    else if (args[i] == "--sevenzip" && i + 1 < args.Length)
        sevenZip = args[++i];
}

// ── Merge Datfiles mode (writes merged dats; byte-parity target) ─────────
if (mergeRoot is not null)
{
    string category = Path.GetFileName(Path.TrimEndingDirectorySeparator(mergeRoot));
    foreach (var job in MergeDatfiles.ScanForMerge(mergeRoot).Where(j => j.Action == "merge"))
    {
        var (merged, header, err) = MergeDatfiles.CollectGames(job.Path, job.Deeper);
        if (err.Length > 0)
        {
            Console.WriteLine($"merge|{job.Name}|ERROR|{err}");
            continue;
        }
        string datName = category + " - " + job.Name;
        string outFn = DatWriter.MakeDatFilename(datName, mergeDate);
        string outPath = Path.Combine(job.Path, outFn);
        MergeDatfiles.WriteMergedDat(outPath, datName, merged, header, mergeDate);
        int romTotal = merged.Sum(kv => kv.Value.Count);
        Console.WriteLine($"merge|{job.Name}|{merged.Count}|{romTotal}|"
            + Path.GetRelativePath(mergeRoot, outPath).Replace('\\', '/'));
    }
    return 0;
}

// ── ZIP Store Packer mode (packs in place, then lists zip entries) ───────
if (packDir is not null)
{
    var exts = packExts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(e => "." + e.TrimStart('.').ToLowerInvariant()).ToList();
    var cb = new ArchiveLog();
    ZipStorePacker.Pack(packDir, exts, recurse: true, verify: true, skipExisting: true,
                        cb, CancellationToken.None);
    // Canonical listing of every produced zip
    foreach (string zip in Directory.EnumerateFiles(packDir, "*.zip", SearchOption.AllDirectories)
                 .OrderBy(x => x, StringComparer.Ordinal))
    {
        using var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read);
        foreach (var e in ZipCentralDirectory.Read(fs))
            Console.WriteLine($"zip|{Path.GetRelativePath(packDir, zip).Replace('\\', '/')}"
                + $"|{e.FileName}|{e.UncompressedSize}|{e.Crc32:x8}|{e.Method}");
    }
    return 0;
}

// ── Recursive Archive Extractor mode (extract in place, then list tree) ──
if (extractDir is not null)
{
    var cb = new ArchiveLog();
    ArchiveExtractor.Extract(sevenZip, extractDir, dstRoot: null,
        exts: [".zip", ".7z", ".rar"], afterMode: "keep", moveRoot: null,
        recurse: true, autoNested: false, cb, CancellationToken.None);
    foreach (string f in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories)
                 .OrderBy(x => x, StringComparer.Ordinal))
        Console.WriteLine($"tree|{Path.GetRelativePath(extractDir, f).Replace('\\', '/')}|{new FileInfo(f).Length}");
    return 0;
}

// ── Counter mode ─────────────────────────────────────────────────────────
if (countDir is not null)
{
    foreach (string fp in DatValidator.CollectFiles(countDir, singleMode: false))
    {
        string rel = Path.GetRelativePath(countDir, fp).Replace('\\', '/');
        var c = DatCounter.ScanDatCounts(fp);
        Console.WriteLine($"count|{rel}|{c.DatName}|{c.Games}|{c.Roms}|{c.TotalBytes}|{c.DirCount}|{c.Error}");
    }
    return 0;
}

// ── Validator mode ───────────────────────────────────────────────────────
if (validatePath is not null)
{
    var files = DatValidator.CollectFiles(validatePath, singleMode: File.Exists(validatePath));
    int totalIssues = 0, totalRoms = 0;
    foreach (string fp in files)
    {
        Console.WriteLine("file|" + Path.GetFileName(fp));
        var (issues, roms) = DatValidator.ValidateFile(fp, Console.WriteLine, () => false);
        Console.WriteLine($"result|{issues}|{roms}");
        totalIssues += issues;
        totalRoms += roms;
    }
    Console.WriteLine($"total|{files.Count}|{totalRoms}|{totalIssues}");
    return 0;
}

// ── Bulk Header Updater mode ─────────────────────────────────────────────
if (bhuTarget is not null)
{
    var fieldValues = new Dictionary<string, string?>();
    foreach (string f in BulkHeaderUpdater.OptionalFields)
        fieldValues[f] = bhuSet.GetValueOrDefault(f);
    foreach (string fp in BulkHeaderUpdater.IterDatFiles(bhuTarget))
    {
        var d = BulkHeaderUpdater.UpdateFile(fp, bhuDate, fieldValues, bhuFp);
        string relAfter = Path.GetRelativePath(bhuTarget, d.PathAfter).Replace('\\', '/');
        Console.WriteLine($"bhu|{relAfter}"
            + $"|fn={d.FnDateBefore ?? "None"}>{d.FnDateAfter ?? "None"}"
            + $"|hdr={d.HdrDateBefore ?? "None"}>{d.HdrDateAfter ?? "None"}"
            + $"|added={string.Join(",", d.FieldsAdded)}"
            + $"|updated={string.Join(",", d.FieldsUpdated)}"
            + $"|cleared={string.Join(",", d.FieldsCleared)}"
            + $"|renamed={(d.Renamed ? 1 : 0)}|content={(d.ContentUpdated ? 1 : 0)}"
            + $"|warn={string.Join(";", d.Warnings)}");
    }
    return 0;
}

// ── Analyzer mode: print structured findings for parity comparison ──────
if (analyzeDir is not null)
{
    var f = FolderAnalysis.Analyze(analyzeDir, analyzeType);
    var ps = FolderAnalysis.CollectPathLengths(analyzeDir);
    Console.WriteLine("top_folders=" + f.TopFolders);
    Console.WriteLine("total_items=" + f.TotalItems);
    Console.WriteLine("max_depth=" + f.MaxDepth);
    Console.WriteLine("flat_games=" + f.FoldersFlatGames);
    Console.WriteLine("with_direct=" + f.FoldersWithDirectItems);
    Console.WriteLine("containers=" + f.FoldersAsContainers);
    Console.WriteLine("nested=" + f.FoldersWithNestedSubdirs);
    Console.WriteLine("empty=" + f.FoldersEmpty);
    Console.WriteLine("histogram=" + string.Join(",",
        f.DepthHistogram.Select(kv => kv.Key + ":" + kv.Value)));
    foreach (string note in f.Notes)
        Console.WriteLine("note=" + note);
    Console.WriteLine("rec_gen=" + f.Recommendation.GenMode);
    Console.WriteLine("rec_structure=" + f.Recommendation.Structure);
    Console.WriteLine("rec_confidence=" + f.Recommendation.Confidence);
    Console.WriteLine("rec_summary=" + f.Recommendation.Summary);
    foreach (string d in f.Recommendation.Detail)
        Console.WriteLine("detail=" + d);
    Console.WriteLine("path_total=" + ps.TotalPaths);
    Console.WriteLine("path_max=" + ps.MaxPathLen);
    Console.WriteLine("warn_count=" + ps.WarnCount);
    Console.WriteLine("crit_count=" + ps.CritCount);
    return 0;
}

if (settingsPath is null || !File.Exists(settingsPath))
{
    Console.Error.WriteLine("Usage: ParityRunner --settings <file.json>");
    return 2;
}

var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
var root = doc.RootElement;

string Str(string key, string def = "") =>
    root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
        ? v.GetString() ?? def : def;
bool Flag(string key, bool def = false) =>
    root.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? v.GetBoolean() : def;
int Int(string key, int def = 0) =>
    root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetInt32() : def;

var settings = new DatSettings
{
    InputRoot = Str("input_root"),
    OutputRoot = Str("output_root"),
    ParentName = Str("parent_name"),
    Description = Str("description"),
    Category = Str("category"),
    Version = Str("version"),
    Date = Str("date"),
    Author = Str("author"),
    Url = Str("url"),
    Homepage = Str("homepage"),
    Comment = Str("comment"),
    DatType = Str("dat_type", "mixed"),
    GenMode = Str("gen_mode", "per_root"),
    Structure = Str("structure", "opt2"),
    UseMachine = Flag("use_machine"),
    InclGameDesc = Flag("incl_game_desc", true),
    ForcePacking = Flag("forcepacking", true),
    InclFileDate = Flag("incl_file_date"),
    IncludeMd5 = Flag("include_md5"),
    IncludeSha256 = Flag("include_sha256"),
    IncludeBlake3 = Flag("include_blake3"),
    ExtInclude = Str("ext_include"),
    ExtExclude = Str("ext_exclude"),
    Multithread = Flag("multithread", true),
    Threads = Int("threads", 4),
    Incremental = Flag("incremental"),
    IncrementalDatPath = Str("incremental_dat_path"),
    RetireOldDats = Flag("retire_old_dats", true),
};

bool failed = false;
var callbacks = new EngineCallbacks
{
    Status = msg => Console.WriteLine("[status] " + msg),
    Totals = (jobs, items) => Console.WriteLine($"[totals] {jobs} jobs, {items} items"),
    Folder = (path, n) => Console.WriteLine($"[folder] {path} ({n} items)"),
    ItemHashed = (name, detail) => Console.WriteLine($"[ok] {name}{detail}"),
    ItemCarried = name => Console.WriteLine($"[carried] {name}"),
    ItemError = (name, detail) => Console.WriteLine($"[err] {name} :: {detail}"),
    DatWritten = (path, n) => Console.WriteLine($"[dat] {path}"),
    Done = (ok, errors, done, total, dats, elapsed) =>
    {
        foreach (string e in errors)
            Console.WriteLine("[error] " + e);
        Console.WriteLine($"[done] ok={ok} items={done}/{total} dats={dats} elapsed={elapsed:F1}s");
        failed = !ok || errors.Count > 0;
    },
};

var previews = previewDir is null ? null : new List<PreviewEntry>();
DatEngine.Run(settings, callbacks, CancellationToken.None, CancellationToken.None, previews);

if (previewDir is not null && previews is not null)
{
    Directory.CreateDirectory(previewDir);
    foreach (var entry in previews)
    {
        foreach (string opt in (string[])["opt2", "opt3", "opt4"])
        {
            string xml = PreviewRenderer.Render(entry, opt);
            string name = $"{XmlText.SafeFilename(entry.DatName)}__{opt}.xml";
            File.WriteAllText(Path.Combine(previewDir, name), xml,
                              new System.Text.UTF8Encoding(false));
        }
    }
    Console.WriteLine($"[preview] {previews.Count * 4} render(s) written to {previewDir}");
}

return failed ? 1 : 0;
