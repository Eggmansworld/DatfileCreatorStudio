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
    /// <summary>Item carried forward from the existing dat without rehashing (incremental).</summary>
    public Action<string>? ItemCarried { get; init; }
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
                           CancellationToken softStop, CancellationToken hardStop,
                           List<PreviewEntry>? previewResults = null)
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

        // ── Network bandwidth throttle ───────────────────────────────────
        // Only applied when reading from a network path (UNC / mapped
        // network drive) — local NVMe/SATA/USB drives never need it.
        bool pathIsNet = NetworkInfo.IsNetworkPath(inputRoot);
        double netRate = !pathIsNet
            ? 0.0
            : s.NetCapMbps > 0
                ? s.NetCapMbps * 1_000_000.0 / 8
                : NetworkInfo.DetectNetCapBytesPerSec(0.85);
        var throttle = netRate > 0 ? new BandwidthThrottle(netRate) : null;

        string capSuffix = "  |  BytesIO threshold: "
            + (ZipAnalyzer.BytesIoThreshold / (1024 * 1024)) + " MB"
            + "  |  Large-zip serialised (SMB lock active)";
        if (!pathIsNet)
            cb.Status?.Invoke("Network cap: N/A (local path — throttle disabled)" + capSuffix);
        else if (netRate > 0)
            cb.Status?.Invoke("Network cap: " + (netRate / (1_000_000.0 / 8)).ToString("F0")
                + " Mbit/s  (" + (s.NetCapMbps == 0 ? "auto-detected" : "manual") + ")" + capSuffix);
        else
            cb.Status?.Invoke("Network cap: unlimited  (no NIC speed detected)" + capSuffix);

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
        int totalCarried = 0;
        int totalHashed = 0;

        // ── Build incremental index map (if incremental mode) ────────────
        // Maps outDir → (gameIndex, existingDatPath) for each job.
        var incrIndexMap = new Dictionary<string, (DatGameIndex Index, string DatPath)>(StringComparer.Ordinal);
        if (s.Incremental && s.IncrementalDatPath.Length > 0)
        {
            string datSrc = Path.GetFullPath(s.IncrementalDatPath);

            // Job lookup tables — needed for both file and folder modes
            var jobFolderPathMap = new Dictionary<string, Job>(StringComparer.Ordinal);
            var jobNameMap = new Dictionary<string, Job>(StringComparer.Ordinal);
            foreach (var job in jobs)
            {
                jobFolderPathMap[job.FolderPath.ToLowerInvariant()] = job;
                string relFp;
                try
                {
                    relFp = Path.GetRelativePath(inputRoot, job.FolderPath);
                }
                catch
                {
                    relFp = ".";
                }
                string compound = relFp is "." or ""
                    ? Path.GetFileName(Path.TrimEndingDirectorySeparator(job.FolderPath))
                    : string.Join(" - ", relFp.Replace('\\', '/').Split('/'));
                jobNameMap[DatWriter.MakeDatName(compound, inputRoot, s)] = job;
            }

            // Match a dat's <name> header to a job: exact make_dat_name()
            // match first, then each " - " part (right to left) tried as a
            // direct subfolder of the input root — matches even when the
            // parent name or root depth differ between runs
            Job? MatchDatToJob(string datNameHdr)
            {
                if (datNameHdr.Length == 0)
                    return null;
                if (jobNameMap.TryGetValue(datNameHdr, out var exact))
                    return exact;
                var parts = datNameHdr.Split(" - ").Select(p => p.Trim()).ToArray();
                foreach (string p in parts.Reverse())
                {
                    if (p.Length == 0)
                        continue;
                    string cand = Path.Combine(inputRoot, p).ToLowerInvariant();
                    if (jobFolderPathMap.TryGetValue(cand, out var byPath))
                        return byPath;
                }
                return null;
            }

            if (File.Exists(datSrc))
            {
                var (gi, hd, errS) = IncrementalUpdate.ReadDatIndex(datSrc);
                if (errS.Length > 0)
                {
                    errors.Add("Could not read dat: " + datSrc + " :: " + errS);
                }
                else if (jobs.Count > 0)
                {
                    var match = MatchDatToJob(hd.GetValueOrDefault("name", ""));
                    if (match is not null)
                    {
                        incrIndexMap[match.OutDir] = (gi, datSrc);
                    }
                    else
                    {
                        // No specific job matched — assume the dat covers the
                        // entire input root. Carry-forward is keyed by
                        // filename, so only matching items are carried.
                        foreach (var job in jobs)
                            incrIndexMap[job.OutDir] = (gi, datSrc);
                    }
                }
            }
            else if (Directory.Exists(datSrc))
            {
                void WalkDats(string dir)
                {
                    string[] files;
                    string[] subdirs;
                    try
                    {
                        files = Directory.GetFiles(dir);
                        subdirs = Directory.GetDirectories(dir);
                    }
                    catch
                    {
                        return;
                    }
                    foreach (string full in files.OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
                    {
                        string fn = Path.GetFileName(full);
                        if (!fn.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                            && !fn.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var (gi, hd, errS) = IncrementalUpdate.ReadDatIndex(full);
                        if (errS.Length > 0)
                        {
                            errors.Add("Could not read dat: " + full + " :: " + errS);
                            continue;
                        }

                        bool matched = false;
                        string relDir;
                        try
                        {
                            relDir = Path.GetRelativePath(datSrc, dir);
                        }
                        catch
                        {
                            relDir = ".";
                        }
                        if (relDir.Length > 0 && relDir != ".")
                        {
                            string key = Path.Combine(inputRoot, relDir).ToLowerInvariant();
                            if (jobFolderPathMap.TryGetValue(key, out var byPath))
                            {
                                incrIndexMap[byPath.OutDir] = (gi, full);
                                matched = true;
                            }
                        }
                        if (!matched)
                        {
                            var match = MatchDatToJob(hd.GetValueOrDefault("name", ""));
                            if (match is not null)
                            {
                                incrIndexMap[match.OutDir] = (gi, full);
                            }
                            else
                            {
                                // Likely covers the entire input root — map to
                                // every unclaimed job
                                foreach (var job in jobs.Where(j => !incrIndexMap.ContainsKey(j.OutDir)))
                                    incrIndexMap[job.OutDir] = (gi, full);
                            }
                        }
                    }
                    foreach (string sub in subdirs)
                        WalkDats(sub);
                }
                WalkDats(datSrc);
            }
        }

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

            // ── Incremental: look up existing dat index for this job ─────
            DatGameIndex? jobGameIndex = null;
            string jobExistingDat = "";
            if (s.Incremental)
            {
                if (incrIndexMap.TryGetValue(job.OutDir, out var incrPair))
                {
                    jobGameIndex = incrPair.Index;
                    jobExistingDat = incrPair.DatPath;
                }
                else
                {
                    cb.Status?.Invoke($"No existing dat found for {folderName} — full hash.");
                }
            }

            // ── Hash / analyze all items ─────────────────────────────────
            var data = new DatData();
            bool incomplete = false;
            bool incrementalUsed = false;

            if (s.Incremental && jobGameIndex is not null && jobGameIndex.Games.Count > 0)
            {
                // Incremental: carry forward unchanged items, hash only new
                // or changed ones — single-threaded in tree order, exactly
                // like the suite (items are NOT re-sorted in this mode)
                var (incrData, incrDone, jobCarried, jobHashed, jobErrs) =
                    IncrementalUpdate.BuildIncrementalData(items, jobGameIndex, s,
                                                           hardStop, cb, doneItems, throttle);
                data = incrData;
                doneItems = incrDone;
                errors.AddRange(jobErrs);
                totalCarried += jobCarried;
                totalHashed += jobHashed;
                if (hardStop.IsCancellationRequested)
                    incomplete = true;
                incrementalUsed = true;
            }

            // Sort items by (parent dir, basename) so items from the same
            // subfolder are processed consecutively (full-hash mode only)
            if (!incrementalUsed)
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
                                                    s.IncludeBlake3, hardStop,
                                                    throttle: throttle), null);
                    var (list, diag) = ZipAnalyzer.Analyze(itemPath, s.IncludeMd5, s.IncludeSha256,
                                                           s.InclFileDate, s.IncludeBlake3, hardStop,
                                                           throttle);
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

            if (incrementalUsed)
            {
                // Hashing already done by the incremental builder above
            }
            else if (maxWorkers == 1)
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

                // Incremental: optionally rename the superseded dat to .old
                if (s.Incremental && s.RetireOldDats
                    && jobExistingDat.Length > 0 && File.Exists(jobExistingDat))
                {
                    var (oldFinal, renameErr) = IncrementalUpdate.RetireOldDat(jobExistingDat);
                    if (renameErr.Length > 0)
                        errors.Add(renameErr);
                    else
                        cb.Status?.Invoke($"Retired: {Path.GetFileName(oldFinal)}");
                }
            }

            // Store for the preview window (only complete jobs with data)
            if (previewResults is not null && !incomplete
                && (data.Mixed.Count > 0 || data.Zipped.Count > 0))
            {
                previewResults.Add(new PreviewEntry
                {
                    DatName = datName,
                    HeaderDate = headerDate,
                    Node = job.Node,
                    Data = data,
                    Settings = s.Clone(),
                    IsTree = isTree,
                });
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
        if (s.Incremental && totalCarried + totalHashed > 0)
            cb.Status?.Invoke($"Incremental summary: {totalCarried} carried, "
                              + $"{totalHashed} hashed, {doneItems} total items.");
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
