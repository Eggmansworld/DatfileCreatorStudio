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
    DatFormat = Str("dat_format", "modern"),
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
        foreach (string opt in (string[])["opt1", "opt2", "opt3", "opt4"])
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
