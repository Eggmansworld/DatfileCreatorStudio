namespace DatfileCreator.Core;

/// <summary>Colour tag for one pre-flight result line.</summary>
public enum PreflightTag { Plain, Ok, Warn, Err, Dim }

/// <summary>Outcome of a pre-flight validation pass.</summary>
public sealed class PreflightResult
{
    /// <summary>True when every dat validated at or above the warn threshold.</summary>
    public bool AllOk { get; init; }
    /// <summary>True when the user stopped the scan early.</summary>
    public bool Stopped { get; init; }
    /// <summary>True when validation could not run at all (bad paths, no dats).</summary>
    public bool Failed { get; init; }
    /// <summary>Full detail log (every anomaly), for Save Pre-inspection Log.</summary>
    public List<string> InspectionLog { get; init; } = [];
}

/// <summary>
/// Incremental update pre-flight validation, ported from the suite's
/// IncrementalConfirmDialog worker: cross-checks every dat in the source
/// against the input folder tree and reports per-dat match percentages.
/// Screen output goes through <c>post</c> (whole lines with a colour tag);
/// the full anomaly detail accumulates in the returned inspection log.
/// </summary>
public static class PreflightCheck
{
    /// <summary>Warn when a dat's match percentage falls below this.</summary>
    public const double Threshold = 80.0;

    public static PreflightResult Run(DatSettings s, Action<string, PreflightTag> post,
                                      CancellationToken stop)
    {
        var log = new List<string>();
        string srcPath = s.IncrementalDatPath.Trim();
        string root = s.InputRoot.Trim();

        PreflightResult Fail(string message)
        {
            post(message, PreflightTag.Err);
            return new PreflightResult { Failed = true, InspectionLog = log };
        }

        if (srcPath.Length == 0)
            return Fail("No dat source specified. Please set the dat path before starting.");
        if (root.Length == 0 || !Directory.Exists(root))
            return Fail("Input root folder not found.");

        // ── Collect dat files to validate ────────────────────────────────
        var datEntries = new List<(string Path, string RelDir)>();
        if (File.Exists(srcPath)
            && (srcPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || srcPath.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)))
        {
            datEntries.Add((srcPath, "."));
        }
        else if (Directory.Exists(srcPath))
        {
            void Walk(string dir)
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
                    if ((fn.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                         || fn.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                        && !fn.EndsWith(".old", StringComparison.OrdinalIgnoreCase))
                    {
                        string relDir;
                        try
                        {
                            relDir = Path.GetRelativePath(srcPath, dir);
                        }
                        catch
                        {
                            relDir = ".";
                        }
                        datEntries.Add((full, relDir));
                    }
                }
                foreach (string sub in subdirs)
                    Walk(sub);
            }
            Walk(srcPath);
        }
        else
        {
            return Fail("Dat source not found or not a valid .xml/.dat file/folder:\n  " + srcPath);
        }

        if (datEntries.Count == 0)
            return Fail("No dat files found in the specified folder.");

        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string typeLabel = s.DatType == "mixed" ? "Mixed (Archive as File)" : "Zipped";
        log.Add(new string('=', 72));
        log.Add("Datfile Creator Studio — Pre-inspection Log");
        log.Add("Generated  : " + ts);
        log.Add("Dat source : " + srcPath);
        log.Add("Input root : " + root);
        log.Add("Dat type   : " + typeLabel);
        log.Add(new string('=', 72));
        log.Add("");

        post($"Found {datEntries.Count} dat file(s) to validate.", PreflightTag.Plain);
        post("", PreflightTag.Plain);
        log.Add($"Found {datEntries.Count} dat file(s) to validate.");
        log.Add("");

        bool allOk = true;
        int totalMissing = 0;
        int totalNew = 0;
        int totalFm = 0;

        foreach (var (datPath, relDir) in datEntries
                     .OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            if (stop.IsCancellationRequested)
            {
                log.Add("");
                log.Add($"*** Scan stopped by user after {totalMissing + totalNew + totalFm} issues noted ***");
                post("", PreflightTag.Plain);
                post("Scan stopped by user. Partial results shown above.", PreflightTag.Warn);
                return new PreflightResult { AllOk = false, Stopped = true, InspectionLog = log };
            }

            var (gi, hd, errS) = IncrementalUpdate.ReadDatIndex(datPath);
            if (errS.Length > 0)
            {
                string msg = "  ERROR reading " + Path.GetFileName(datPath) + ": " + errS;
                post(msg, PreflightTag.Err);
                log.Add(msg);
                allOk = false;
                continue;
            }

            string datNameHdr = hd.GetValueOrDefault("name", Path.GetFileName(datPath));
            post("  " + datNameHdr, PreflightTag.Dim);
            log.Add("");
            log.Add("DAT: " + datNameHdr);
            log.Add("File: " + datPath);

            // Map the dat to its source folder: mirrored relative path first,
            // then the dat-name heuristic (each " - " part, right to left)
            string? folderCandidate;
            if (relDir.Length > 0 && relDir != ".")
            {
                folderCandidate = Path.Combine(root, relDir);
            }
            else
            {
                folderCandidate = null;
                var parts = datNameHdr.Split(" - ").Select(p => p.Trim()).ToArray();
                foreach (string p in parts.Reverse())
                {
                    if (p.Length == 0)
                        continue;
                    string candidate = Path.Combine(root, p);
                    if (Directory.Exists(candidate))
                    {
                        folderCandidate = candidate;
                        break;
                    }
                }
                folderCandidate ??= root;
            }

            if (!Directory.Exists(folderCandidate))
            {
                string msg = "    [??] Source folder not found: " + folderCandidate;
                post(msg, PreflightTag.Warn);
                log.Add(msg);
                allOk = false;
                continue;
            }

            log.Add("Folder: " + folderCandidate);

            var vr = IncrementalUpdate.ValidateDatVsFolder(gi, folderCandidate, s.DatType);
            double pct = vr.MatchPct;

            string sym = pct >= Threshold ? "OK" : "!!";
            if (pct < Threshold)
                allOk = false;

            string line = $"    [{sym}] {vr.FoundInFolder}/{vr.TotalInDat} entries found in folder ({pct:F1}% match)";
            if (vr.Extra.Count > 0)
                line += $"  |  {vr.Extra.Count} new item(s) to add";
            post(line, sym == "OK" ? PreflightTag.Ok : PreflightTag.Warn);

            log.Add($"Result : [{sym}] {vr.FoundInFolder}/{vr.TotalInDat} entries ({pct:F1}% match)");

            var gm = vr.Missing;
            var fm = vr.FileMissing;
            if (gm.Count > 0)
            {
                string missNames = string.Join(", ", gm.Take(3)) + (gm.Count > 3 ? "..." : "");
                post("    Missing games/files: " + missNames, PreflightTag.Warn);
            }
            if (fm.Count > 0)
                post($"    Missing rom files  : {fm.Count} file(s). First: {fm[0]}", PreflightTag.Warn);

            // Log: FULL detail — every anomaly
            if (gm.Count > 0)
            {
                log.Add($"  Missing game entries ({gm.Count}):");
                foreach (string g in gm)
                    log.Add("    - " + g);
                totalMissing += gm.Count;
            }
            if (fm.Count > 0)
            {
                log.Add($"  Missing rom files ({fm.Count}):");
                foreach (string f in fm)
                    log.Add("    - " + f);
                totalFm += fm.Count;
            }
            if (vr.Extra.Count > 0)
            {
                log.Add($"  New items in folder (not in dat) ({vr.Extra.Count}):");
                foreach (string e in vr.Extra.Take(200))
                    log.Add("    + " + e);
                if (vr.Extra.Count > 200)
                    log.Add($"    ... ({vr.Extra.Count - 200} more not shown)");
                totalNew += vr.Extra.Count;
            }
            if (gm.Count == 0 && fm.Count == 0 && vr.Extra.Count == 0)
                log.Add("  No anomalies — fully matched.");
        }

        log.Add("");
        log.Add(new string('=', 72));
        log.Add("SUMMARY");
        log.Add("  Dat files scanned      : " + datEntries.Count);
        log.Add("  Missing game entries   : " + totalMissing);
        log.Add("  Missing rom files      : " + totalFm);
        log.Add("  New items (to be added): " + totalNew);
        log.Add("  Overall status         : " + (allOk ? "PASSED" : "WARNINGS — review before proceeding"));
        log.Add(new string('=', 72));

        post("", PreflightTag.Plain);
        if (allOk)
            post("Validation passed. Ready to proceed.", PreflightTag.Ok);
        else
            post("Some dats have low match rates. Review before proceeding. You can still proceed — "
                 + "entries not found in the folder will be removed from the dat.", PreflightTag.Warn);

        return new PreflightResult { AllOk = allOk, InspectionLog = log };
    }
}
