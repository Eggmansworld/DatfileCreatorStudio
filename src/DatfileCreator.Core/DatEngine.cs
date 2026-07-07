using System.Diagnostics;

namespace DatfileCreator.Core;

/// <summary>
/// Progress and log callbacks raised by the engine. All callbacks may be
/// invoked from worker threads — the GUI marshals to the UI thread itself.
/// </summary>
public sealed class EngineCallbacks
{
    /// <summary>A folder is being scanned during Phase 1 (path, depth).</summary>
    public Action<string, int>? Scan { get; init; }
    /// <summary>General status line (phase changes, notes).</summary>
    public Action<string>? Status { get; init; }
    /// <summary>Phase 1 complete: (total jobs, total items).</summary>
    public Action<int, int>? Totals { get; init; }
    /// <summary>A job folder started: (folder path, item count).</summary>
    public Action<string, int>? Folder { get; init; }
    /// <summary>Processing entered a new subfolder within the job (relative path).</summary>
    public Action<string>? Subfolder { get; init; }
    /// <summary>Item completed: (total done so far).</summary>
    public Action<int>? Progress { get; init; }
    /// <summary>Item hashed OK: (basename, diagnostic detail or "").</summary>
    public Action<string, string>? ItemHashed { get; init; }
    /// <summary>Item failed: (basename, error detail).</summary>
    public Action<string, string>? ItemError { get; init; }
    /// <summary>A dat file was written: (path, running count).</summary>
    public Action<string, int>? DatWritten { get; init; }
    /// <summary>Run finished: (ok, errors, doneItems, totalItems, writtenDats, elapsedSeconds).</summary>
    public Action<bool, IReadOnlyList<string>, int, int, int, double>? Done { get; init; }
}

/// <summary>
/// The unified processing engine for all dat types and generation modes —
/// a direct port of the Python suite's process(). Network throttling arrives
/// in Session 2 and incremental update in Session 3.
/// </summary>
public static class DatEngine
{
    private const int MaxSafePath = 240;

    private sealed record Job(string FolderPath, FolderNode Node, string OutDir);

    public static void Run(DatSettings s, EngineCallbacks cb,
                           CancellationToken softStop, CancellationToken hardStop)
    {
        var sw = Stopwatch.StartNew();
        var errors = new List<string>();
        string inputRoot = Path.GetFullPath(s.InputRoot);
        string outputRoot = Path.GetFullPath(s.OutputRoot);
        string headerDate = s.Date.Length > 0 ? s.Date : DateTime.Today.ToString("yyyy-MM-dd");
        bool isMixed = s.DatType == "mixed";
        bool isPertop = s.GenMode == "per_top";
        bool isPerroot = s.GenMode == "per_root";
        bool isTree = isPertop || isPerroot; // one recursive dat per job

        void Fail(string message) =>
            cb.Done?.Invoke(false, [message], 0, 0, 0, sw.Elapsed.TotalSeconds);

        try
        {
            Directory.CreateDirectory(outputRoot);
        }
        catch (Exception e)
        {
            Fail($"Cannot create output root: {e.Message}");
            return;
        }

        int maxWorkers = s.Multithread ? Math.Clamp(s.Threads, 1, 8) : 1;

        cb.Status?.Invoke("Network cap: not yet available in Studio (Session 2) — reads run unthrottled.");
        if (s.Incremental)
            cb.Status?.Invoke("Incremental update arrives in Session 3 — performing a full hash run.");

        // ── Phase 1: discovery ───────────────────────────────────────────
        cb.Status?.Invoke("Phase 1 of 2 — Discovering folders and files... (please wait)");
        List<FileSystemInfo> topEntries;
        try
        {
            var infos = new DirectoryInfo(inputRoot).GetFileSystemInfos();
            Array.Sort(infos, (a, b) =>
                string.CompareOrdinal(a.Name.ToLowerInvariant(), b.Name.ToLowerInvariant()));
            topEntries = [.. infos];
        }
        catch (Exception e)
        {
            Fail($"Cannot scan input: {e.GetType().Name}: {e.Message}");
            return;
        }

        var jobs = new List<Job>();
        var extInc = isMixed ? ExtensionFilter.Parse(s.ExtInclude) : [];
        var extExc = isMixed ? ExtensionFilter.Parse(s.ExtExclude) : [];

        // Output always mirrors input structure under a folder named after input_root
        string rootFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(inputRoot));
        string rootOutBase = Path.Combine(outputRoot, rootFolderName);

        // ── Mode: 1 dat per top-level folder (single dat) ────────────────
        if (isPertop)
        {
            var topNode = isMixed
                ? FolderScanner.ScanTreeMixed(inputRoot, "", hardStop, cb.Scan, extInc, extExc)
                : FolderScanner.ScanTreeZipped(inputRoot, "", hardStop, cb.Scan);
            topNode.Name = rootFolderName;
            if (FolderScanner.CountItems(topNode) > 0)
                jobs.Add(new Job(inputRoot, topNode, outputRoot));
        }

        // ── Root-level items (files/zips sitting directly in input_root) ─
        var rootItems = new List<string>();
        foreach (var entry in topEntries)
        {
            if (FolderScanner.IsHiddenOrSystem(entry) || IsSymlink(entry))
                continue;
            if (entry is not FileInfo)
                continue;
            if (isMixed)
            {
                if (extInc.Count == 0 && extExc.Count == 0
                    || ExtensionFilter.Matches(entry.Name, extInc, extExc))
                    rootItems.Add(entry.FullName);
            }
            else if (entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                rootItems.Add(entry.FullName);
            }
        }

        if (rootItems.Count > 0 && !isPertop)
        {
            rootItems.Sort((a, b) => string.CompareOrdinal(
                Path.GetFileName(a).ToLowerInvariant(), Path.GetFileName(b).ToLowerInvariant()));
            var rootNode = new FolderNode { Name = rootFolderName, RelPath = "" };
            rootNode.Items.AddRange(rootItems);
            jobs.Add(new Job(inputRoot, rootNode, rootOutBase));
        }

        // ── Subfolders ───────────────────────────────────────────────────
        foreach (var entry in topEntries)
        {
            if (isPertop || hardStop.IsCancellationRequested)
                break;
            if (FolderScanner.IsHiddenOrSystem(entry) || IsSymlink(entry))
                continue;
            if (entry is not DirectoryInfo)
                continue;

            string folderPath = entry.FullName;
            string folderName = entry.Name;

            if (isPerroot)
            {
                var node = isMixed
                    ? FolderScanner.ScanTreeMixed(folderPath, "", hardStop, cb.Scan, extInc, extExc)
                    : FolderScanner.ScanTreeZipped(folderPath, "", hardStop, cb.Scan);
                if (FolderScanner.CountItems(node) == 0)
                    continue;
                jobs.Add(new Job(folderPath, node, Path.Combine(rootOutBase, folderName)));
            }
            else
            {
                // per_all: one dat per folder that has direct content
                CollectPerAllJobs(folderPath, folderName);
            }
        }

        void CollectPerAllJobs(string dirPath, string relFromRoot)
        {
            if (hardStop.IsCancellationRequested)
                return;
            int scanDepth = relFromRoot.Replace('\\', '/').Split('/').Length - 1;
            cb.Scan?.Invoke(dirPath, scanDepth);

            var shallow = new FolderNode { Name = Path.GetFileName(dirPath), RelPath = relFromRoot };
            var entries = FolderScanner.SortedChildren(dirPath);
            foreach (var e2 in entries)
            {
                if (hardStop.IsCancellationRequested)
                    break;
                if (FolderScanner.IsHiddenOrSystem(e2) || IsSymlink(e2))
                    continue;
                if (isMixed)
                {
                    if (e2 is FileInfo)
                    {
                        if ((extInc.Count > 0 || extExc.Count > 0)
                            && !ExtensionFilter.Matches(e2.Name, extInc, extExc))
                            continue;
                        shallow.Items.Add(e2.FullName);
                    }
                }
                else if (e2 is FileInfo && e2.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    shallow.Items.Add(e2.FullName);
                }
            }
            shallow.Items.Sort((a, b) => string.CompareOrdinal(
                Path.GetFileName(a).ToLowerInvariant(), Path.GetFileName(b).ToLowerInvariant()));
            if (shallow.Items.Count > 0)
                jobs.Add(new Job(dirPath, shallow, Path.Combine(rootOutBase, relFromRoot)));
            foreach (var e2 in entries)
            {
                if (hardStop.IsCancellationRequested)
                    break;
                if (FolderScanner.IsHiddenOrSystem(e2) || IsSymlink(e2))
                    continue;
                if (e2 is DirectoryInfo)
                    CollectPerAllJobs(e2.FullName, Path.Combine(relFromRoot, e2.Name));
            }
        }

        if (hardStop.IsCancellationRequested)
        {
            cb.Done?.Invoke(false, ["Hard stop during scan."], 0, 0, 0, sw.Elapsed.TotalSeconds);
            return;
        }
        if (softStop.IsCancellationRequested)
        {
            cb.Done?.Invoke(false, ["Soft stop during scan."], 0, 0, 0, sw.Elapsed.TotalSeconds);
            return;
        }
        if (jobs.Count == 0)
        {
            cb.Done?.Invoke(false, ["No content found in input folder."], 0, 0, 0, sw.Elapsed.TotalSeconds);
            return;
        }

        int totalItems = jobs.Sum(j => FolderScanner.CountItems(j.Node));
        int totalJobs = jobs.Count;
        cb.Totals?.Invoke(totalJobs, totalItems);
        cb.Status?.Invoke($"Phase 1 complete — found {totalJobs} folder(s), {totalItems} item(s) to process.");
        cb.Status?.Invoke("Phase 2 of 2 — Hashing and writing dat files...");

        int doneItems = 0;
        int writtenDats = 0;

        // ── Process each job ─────────────────────────────────────────────
        foreach (var job in jobs)
        {
            if (hardStop.IsCancellationRequested)
                break;

            string rel;
            try
            {
                rel = Path.GetRelativePath(inputRoot, job.FolderPath);
            }
            catch
            {
                rel = ".";
            }
            string folderName = rel is "." or ""
                ? Path.GetFileName(Path.TrimEndingDirectorySeparator(job.FolderPath))
                : string.Join(" - ", rel.Replace('\\', '/').Split('/'));

            // Single-dat mode: name from the top-level folder only — no parent prefix
            string datName = isPertop
                ? rootFolderName
                : DatWriter.MakeDatName(folderName, inputRoot, s);

            var items = isTree ? FolderScanner.CollectAllItems(job.Node) : [.. job.Node.Items];

            cb.Folder?.Invoke(job.FolderPath, items.Count);

            try
            {
                Directory.CreateDirectory(job.OutDir);
            }
            catch (Exception e)
            {
                errors.Add($"ERROR creating output folder: {job.OutDir} :: {e.Message}");
                continue;
            }

            // ── Hash / analyze all items ─────────────────────────────────
            var data = new DatData();
            bool incomplete = false;

            // Sort items by (parent dir, basename) so items from the same
            // subfolder are processed consecutively
            items.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(
                    (Path.GetDirectoryName(a) ?? "").ToLowerInvariant(),
                    (Path.GetDirectoryName(b) ?? "").ToLowerInvariant());
                return c != 0 ? c : string.CompareOrdinal(
                    Path.GetFileName(a).ToLowerInvariant(),
                    Path.GetFileName(b).ToLowerInvariant());
            });

            var stateLock = new object();
            string? lastSubdir = null;
            bool subdirSeen = false;

            void EmitSubfolderMarker(string itemPath)
            {
                string parent = Path.GetDirectoryName(itemPath) ?? "";
                if (!subdirSeen || parent != lastSubdir)
                {
                    subdirSeen = true;
                    lastSubdir = parent;
                    string relSub;
                    try
                    {
                        relSub = Path.GetRelativePath(job.FolderPath, parent);
                    }
                    catch
                    {
                        relSub = parent;
                    }
                    cb.Subfolder?.Invoke(relSub);
                }
            }

            // Returns (result-or-null, errorDetail-or-null)
            (object? Result, string? Error) SafeWork(string itemPath)
            {
                try
                {
                    if (isMixed)
                        return (FileHasher.HashFile(itemPath, s.IncludeMd5, s.IncludeSha256,
                                                    s.IncludeBlake3, hardStop), null);
                    var (list, diag) = ZipAnalyzer.Analyze(itemPath, s.IncludeMd5, s.IncludeSha256,
                                                           s.InclFileDate, s.IncludeBlake3, hardStop);
                    return ((list, diag), null);
                }
                catch (OperationCanceledException)
                {
                    return (null, "CANCELLED");
                }
                catch (Exception exc)
                {
                    return (null, $"{exc.GetType().Name}: {exc.Message}");
                }
            }

            void HandleCompletion(string itemPath, object? result, string? errDetail)
            {
                string bname = Path.GetFileName(itemPath);
                lock (stateLock)
                {
                    EmitSubfolderMarker(itemPath);
                    if (errDetail == "CANCELLED")
                    {
                        incomplete = true;
                        return;
                    }
                    doneItems++;
                    cb.Progress?.Invoke(doneItems);
                    if (errDetail is not null || result is null)
                    {
                        string detail = errDetail ?? "unknown error";
                        errors.Add("ERROR: " + itemPath + " :: " + detail);
                        cb.ItemError?.Invoke(bname, detail);
                        return;
                    }
                    if (result is FileHashResult fh)
                    {
                        cb.ItemHashed?.Invoke(bname, "");
                        data.Mixed[itemPath] = fh;
                    }
                    else if (result is (List<ZipRomEntry> list, string diag))
                    {
                        cb.ItemHashed?.Invoke(bname, diag.Length > 0 ? "  (" + diag + ")" : "");
                        data.Zipped[itemPath] = list;
                    }
                }
            }

            if (maxWorkers == 1)
            {
                foreach (string item in items)
                {
                    if (hardStop.IsCancellationRequested)
                    {
                        incomplete = true;
                        break;
                    }
                    var (result, errDetail) = SafeWork(item);
                    HandleCompletion(item, result, errDetail);
                    if (incomplete)
                        break;
                }
            }
            else
            {
                try
                {
                    Parallel.ForEach(items,
                        new ParallelOptions { MaxDegreeOfParallelism = maxWorkers },
                        (item, state) =>
                        {
                            if (hardStop.IsCancellationRequested || incomplete)
                            {
                                incomplete = true;
                                state.Stop();
                                return;
                            }
                            var (result, errDetail) = SafeWork(item);
                            HandleCompletion(item, result, errDetail);
                            if (incomplete)
                                state.Stop();
                        });
                }
                catch (AggregateException ae)
                {
                    foreach (var inner in ae.Flatten().InnerExceptions)
                        errors.Add("ERROR: " + inner.Message);
                }
            }

            // ── Write dat ────────────────────────────────────────────────
            string datFilename = DatWriter.MakeDatFilename(datName, headerDate, incomplete);
            string datPath = Path.Combine(job.OutDir, datFilename);

            if (datPath.Length >= MaxSafePath)
                errors.Add($"PATH LENGTH WARNING ({datPath.Length}): {datPath}");

            DatWriter.WriteDat(datPath, job.Node, data, datName, s, headerDate, errors);

            if (File.Exists(datPath))
            {
                writtenDats++;
                cb.DatWritten?.Invoke(datPath, writtenDats);
            }

            if (incomplete)
            {
                errors.Add($"INCOMPLETE dat: {job.FolderPath}");
                break;
            }
            if (softStop.IsCancellationRequested && !hardStop.IsCancellationRequested)
                break;
        }

        bool ok = !hardStop.IsCancellationRequested && !softStop.IsCancellationRequested;
        cb.Done?.Invoke(ok, errors, doneItems, totalItems, writtenDats, sw.Elapsed.TotalSeconds);
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
}
