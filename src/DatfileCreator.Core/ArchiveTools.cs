using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DatfileCreator.Core;

/// <summary>Log/progress callbacks shared by the archive-oriented tools.</summary>
public sealed class ArchiveLog
{
    /// <summary>(tag, message) — tag is one of ok/fail/warn/info/mute/skip/nested.</summary>
    public Action<string, string>? Line { get; init; }
    public Action<string>? Stat { get; init; }
    public Action<double>? Progress { get; init; }

    public void Write(string tag, string message) => Line?.Invoke(tag, message);
}

/// <summary>Thin wrapper over the 7-Zip-ZStandard 7z.exe process.</summary>
public static class SevenZip
{
    public static (int ExitCode, string StdOut, string StdErr) Run(string sevenZipPath, params string[] args)
    {
        var psi = new ProcessStartInfo(sevenZipPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args)
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout, stderr);
    }
}

/// <summary>
/// Recursive ZIP/7Z/RAR extractor, ported from the suite's _au_* helpers.
/// Extraction is delegated to 7z.exe (identical behaviour to the suite); only
/// .zip classification uses the native central-directory reader.
/// </summary>
public static partial class ArchiveExtractor
{
    [GeneratedRegex("[<>:\"/\\\\|?*]")]
    private static partial Regex InvalidWinCharsRegex();

    /// <summary>Replace Windows-illegal chars with '_', trim trailing " ." (falls back to "extracted").</summary>
    public static string Sanitize(string name)
    {
        name = InvalidWinCharsRegex().Replace(name, "_").TrimEnd(' ', '.');
        return name.Length > 0 ? name : "extracted";
    }

    /// <summary>Classify a .zip via the native reader: ("single", entryName) | ("folder", null) | ("bad", null).</summary>
    public static (string Mode, string? SingleName) ClassifyZipNative(string path)
    {
        List<ZipEntryInfo> infos;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            infos = ZipCentralDirectory.Read(fs);
        }
        catch
        {
            return ("bad", null);
        }

        var files = new List<string>();
        bool hasDir = false;
        foreach (var info in infos)
        {
            string n = info.FileName;
            if (n.EndsWith('/'))
            {
                hasDir = true;
                continue;
            }
            if (n.Contains('/'))
                hasDir = true;
            files.Add(n);
        }
        if (files.Count == 1 && !hasDir && !files[0].Contains('/'))
            return ("single", files[0]);
        return ("folder", null);
    }

    public static (string Mode, string? SingleName) Classify(string path, string sevenZip)
    {
        if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return ClassifyZipNative(path);
        var (rc, _, _) = SevenZip.Run(sevenZip, "l", path);
        return rc != 0 ? ("bad", null) : ("folder", null);
    }

    /// <summary>Move everything from src into dst, merging directories and overwriting on collision.</summary>
    public static void MergeDir(string src, string dst)
    {
        foreach (string item in Directory.EnumerateFileSystemEntries(src))
        {
            string name = Path.GetFileName(item);
            string d = Path.Combine(dst, name);
            bool itemIsDir = Directory.Exists(item);
            bool dExists = File.Exists(d) || Directory.Exists(d);

            if (dExists)
            {
                if (Directory.Exists(d) && itemIsDir)
                {
                    MergeDir(item, d);
                    TryDeleteDir(item);
                }
                else
                {
                    if (!Directory.Exists(d))
                        TryDeleteFile(d);
                    else
                        TryDeleteDir(d);
                    MoveEntry(item, d, itemIsDir);
                }
            }
            else
            {
                MoveEntry(item, d, itemIsDir);
            }
        }
    }

    /// <summary>If target holds a single same-named child folder, flatten it up one level.</summary>
    public static void FlattenDoubleNest(string target)
    {
        var children = Directory.EnumerateFileSystemEntries(target).ToList();
        if (children.Count != 1)
            return;
        string only = children[0];
        if (!Directory.Exists(only) || Path.GetFileName(only) != Path.GetFileName(target))
            return;
        MergeDir(only, target);
        TryDeleteDir(only);
    }

    public static (bool Ok, string Error) ExtractSingle(string archive, string outDir, string sevenZip)
    {
        Directory.CreateDirectory(outDir);
        var (rc, _, err) = SevenZip.Run(sevenZip, "e", "-y", "-o" + outDir, archive);
        return rc == 0 ? (true, "") : (false, err.Trim());
    }

    public static (bool Ok, string Error) ExtractToFolder(string archive, string target, string sevenZip)
    {
        Directory.CreateDirectory(target);
        string tmp = Path.Combine(target, "__tmp_extract__");
        if (Directory.Exists(tmp))
            TryDeleteDir(tmp);
        Directory.CreateDirectory(tmp);
        var (rc, _, err) = SevenZip.Run(sevenZip, "x", "-y", "-o" + tmp, archive);
        if (rc != 0)
        {
            TryDeleteDir(tmp);
            return (false, err.Trim());
        }
        MergeDir(tmp, target);
        TryDeleteDir(tmp);
        FlattenDoubleNest(target);
        return (true, "");
    }

    public static (bool Ok, string Error) DeleteArchive(string path, string mode)
    {
        switch (mode)
        {
            case "keep":
                return (true, "");
            case "recycle":
                return SendToRecycleBin(path);
            default:
                try
                {
                    File.Delete(path);
                    return (true, "");
                }
                catch (Exception e)
                {
                    return (false, e.Message);
                }
        }
    }

    public static (bool Ok, string Dest) MoveMirrored(string archive, string srcRoot, string moveRoot)
    {
        try
        {
            string rel = Path.GetRelativePath(srcRoot, archive);
            string dest = Path.Combine(moveRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(archive, dest);
            return (true, dest);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    public static (bool Ok, string Dest) MoveFlat(string archive, string moveRoot)
    {
        try
        {
            Directory.CreateDirectory(moveRoot);
            string stem = Path.GetFileNameWithoutExtension(archive);
            string suffix = Path.GetExtension(archive);
            string dest = Path.Combine(moveRoot, Path.GetFileName(archive));
            int n = 1;
            while (File.Exists(dest) || Directory.Exists(dest))
                dest = Path.Combine(moveRoot, $"{stem}({n++}){suffix}");
            File.Move(archive, dest);
            return (true, dest);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    /// <summary>All files under folder matching any of the given extensions, sorted.</summary>
    public static List<string> ScanForArchives(string folder, HashSet<string> exts)
    {
        var found = new List<string>();
        if (!Directory.Exists(folder))
            return found;
        foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                found.Add(f);
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>Full extraction run — BFS over the archive queue with nested detection.</summary>
    public static void Extract(string sevenZip, string srcRoot, string? dstRoot,
        HashSet<string> exts, string afterMode, string? moveRoot,
        bool recurse, bool autoNested, ArchiveLog cb, CancellationToken stop)
    {
        var t0 = Stopwatch.StartNew();

        var initial = new List<string>();
        foreach (string f in Directory.EnumerateFiles(srcRoot, "*",
                     recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            if (exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                initial.Add(f);
        }
        initial = initial.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (initial.Count == 0)
        {
            cb.Write("info", $"No archives found under: {srcRoot}\n");
            return;
        }

        var queue = new Queue<string>(initial);
        var queuedSet = new HashSet<string>(initial, StringComparer.OrdinalIgnoreCase);
        int totalSeen = initial.Count;
        int processed = 0;

        cb.Write("info", $"Found {totalSeen} archive(s) under: {srcRoot}\n");
        if (dstRoot is not null)
            cb.Write("info", $"Extract destination: {dstRoot}\n");
        if (moveRoot is not null)
            cb.Write("info", $"Move destination ({(afterMode == "move_mirror" ? "mirror" : "flat")}): {moveRoot}\n");
        if (autoNested)
            cb.Write("info", "Auto-extract nested: ON\n");
        cb.Write("info", new string('─', 64) + "\n");

        int ok = 0, fail = 0, bad = 0, nestedTotal = 0;
        string srcName = Path.GetFileName(Path.TrimEndingDirectorySeparator(srcRoot));

        while (queue.Count > 0 && !stop.IsCancellationRequested)
        {
            string arc = queue.Dequeue();
            processed++;
            double elapsed = t0.Elapsed.TotalSeconds;
            double rate = elapsed > 0 ? processed / elapsed : 0;
            int remain = queue.Count;
            double eta = rate > 0 ? remain / rate : 0;
            cb.Stat?.Invoke($"{processed}/{totalSeen}  (+{remain} queued)  |  OK:{ok}  Fail:{fail}  |  "
                + $"{FmtDuration(elapsed)} elapsed  ETA {FmtDuration(eta)}");
            cb.Progress?.Invoke(Math.Min(99, 100.0 * processed / totalSeen));

            var (mode, _) = Classify(arc, sevenZip);
            if (mode == "bad")
            {
                cb.Write("mute", $"[BAD]   {arc}\n");
                bad++;
                continue;
            }

            string relParent;
            bool underSrc;
            try
            {
                string rel = Path.GetRelativePath(srcRoot, arc);
                underSrc = !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
                relParent = Path.GetDirectoryName(rel) ?? "";
            }
            catch
            {
                relParent = "";
                underSrc = false;
            }

            string outDir;
            if (dstRoot is not null && underSrc)
                outDir = mode == "single"
                    ? Path.Combine(dstRoot, srcName, relParent)
                    : Path.Combine(dstRoot, srcName, relParent, Sanitize(Path.GetFileNameWithoutExtension(arc)));
            else
                outDir = mode == "single"
                    ? Path.GetDirectoryName(arc)!
                    : Path.Combine(Path.GetDirectoryName(arc)!, Sanitize(Path.GetFileNameWithoutExtension(arc)));

            var (okEx, err) = mode == "single"
                ? ExtractSingle(arc, outDir, sevenZip)
                : ExtractToFolder(arc, outDir, sevenZip);

            if (!okEx)
            {
                cb.Write("fail", $"[FAIL]  {Path.GetFileName(arc)}\n        {err}\n");
                fail++;
                continue;
            }

            var nestedFound = ScanForArchives(outDir, exts);
            if (nestedFound.Count > 0)
            {
                nestedTotal += nestedFound.Count;
                cb.Write("nested", new string('▼', 60) + "\n"
                    + $"  ⚠  NESTED ARCHIVES in: {Path.GetFileName(outDir)}\n"
                    + $"  ↳  {nestedFound.Count} archive(s) after extracting {Path.GetFileName(arc)}\n");
                foreach (string nf in nestedFound)
                {
                    if (autoNested && queuedSet.Add(nf))
                    {
                        queue.Enqueue(nf);
                        totalSeen++;
                        cb.Write("nested", $"       [QUEUED]  {Path.GetFileName(nf)}\n");
                    }
                    else
                    {
                        string action = queuedSet.Contains(nf) ? "(already queued)" : "(not auto-extracting)";
                        cb.Write("nested", $"       [FOUND]   {Path.GetFileName(nf)}  {action}\n");
                    }
                }
                cb.Write("nested", new string('▼', 60) + "\n");
            }

            (string suffix, string tag) = afterMode switch
            {
                "keep" => ("", "ok"),
                "recycle" => DeleteArchive(arc, "recycle") is var (rok, re)
                    ? (rok ? "  [recycled]" : $"  [recycle WARN: {re}]", rok ? "ok" : "warn") : ("", "ok"),
                "permanent" => DeleteArchive(arc, "permanent") is var (dok, de)
                    ? (dok ? "  [deleted]" : $"  [delete WARN: {de}]", dok ? "ok" : "warn") : ("", "ok"),
                "move_mirror" => MoveMirrored(arc, srcRoot, moveRoot!) is var (mok, md)
                    ? (mok ? $"  [→ {md}]" : $"  [move WARN: {md}]", mok ? "ok" : "warn") : ("", "ok"),
                "move_flat" => MoveFlat(arc, moveRoot!) is var (fok, fd)
                    ? (fok ? $"  [→ {fd}]" : $"  [move WARN: {fd}]", fok ? "ok" : "warn") : ("", "ok"),
                _ => ("", "ok"),
            };

            cb.Write(tag, $"[OK]    {Path.GetFileName(arc)}  →  {outDir}{suffix}\n");
            ok++;
        }

        if (stop.IsCancellationRequested)
            cb.Write("warn", $"[STOPPED — {queue.Count} remaining]\n");

        cb.Write("info", new string('─', 64) + "\n");
        string nn = nestedTotal > 0 ? $"  |  Nested alerts: {nestedTotal}" : "";
        cb.Write("info", $"Done.  OK: {ok}  Fail: {fail}  Bad: {bad}{nn}  |  {FmtDuration(t0.Elapsed.TotalSeconds)}\n");
        cb.Stat?.Invoke($"Done — OK:{ok}  Fail:{fail}  Bad:{bad}");
        cb.Progress?.Invoke(100);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void MoveEntry(string src, string dest, bool isDir)
    {
        if (isDir)
            Directory.Move(src, dest);
        else
            File.Move(src, dest);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    internal static string FmtDuration(double seconds) =>
        TimeSpan.FromSeconds((int)seconds).ToString(@"h\:mm\:ss");

    // ── Recycle Bin (Windows) ────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct lpFileOp);

    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoErrorUi = 0x0400;

    public static (bool Ok, string Error) SendToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Recycle Bin is only available on Windows");
        try
        {
            var op = new ShFileOpStruct
            {
                wFunc = FoDelete,
                pFrom = Path.GetFullPath(path) + "\0", // marshaller adds the final null → double-null
                fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
            };
            int rc = SHFileOperationW(ref op);
            return rc == 0 ? (true, "") : (false, $"SHFileOperation returned {rc}");
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }
}

/// <summary>
/// ZIP Store Packer, ported from the suite: wraps files into ZIP_STORED
/// containers, verifies before deleting the original.
/// </summary>
public static class ZipStorePacker
{
    public static void Pack(string src, IReadOnlyList<string> exts,
        bool recurse, bool verify, bool skipExisting, ArchiveLog cb, CancellationToken stop)
    {
        var t0 = Stopwatch.StartNew();
        var extSet = exts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (string f in Directory.EnumerateFiles(src, "*",
                     recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            if (extSet.Contains(Path.GetExtension(f).ToLowerInvariant()))
                files.Add(f);
        }
        files = files.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        int total = files.Count;
        if (total == 0)
        {
            cb.Write("info", $"No matching files found under: {src}\n");
            return;
        }

        cb.Write("info", $"Found {total} file(s) under: {src}\nExtensions: {string.Join(", ", exts)}\n");
        cb.Write("info", new string('─', 64) + "\n");
        int ok = 0, fail = 0, skipped = 0;

        for (int i = 0; i < files.Count; i++)
        {
            if (stop.IsCancellationRequested)
            {
                cb.Write("warn", $"[STOPPED at {i}/{total}]\n");
                break;
            }
            string fp = files[i];
            double elapsed = t0.Elapsed.TotalSeconds;
            double rate = elapsed > 0 ? (i + 1) / elapsed : 0;
            double eta = rate > 0 ? (total - i - 1) / rate : 0;
            cb.Stat?.Invoke($"{i + 1}/{total}  |  OK:{ok}  Fail:{fail}  Skip:{skipped}  |  "
                + $"{ArchiveExtractor.FmtDuration(elapsed)} elapsed  ETA {ArchiveExtractor.FmtDuration(eta)}");
            cb.Progress?.Invoke(100.0 * (i + 1) / total);

            string zipPath = Path.ChangeExtension(fp, ".zip");
            if (skipExisting && File.Exists(zipPath))
            {
                cb.Write("skip", $"[SKIP]  {Path.GetFileName(fp)}  (zip already exists)\n");
                skipped++;
                continue;
            }

            string entryName = Path.GetFileName(fp);
            long srcSize;
            try
            {
                srcSize = new FileInfo(fp).Length;
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(fp, entryName, CompressionLevel.NoCompression);
            }
            catch (Exception e)
            {
                cb.Write("fail", $"[FAIL]  {Path.GetFileName(fp)}: create: {e.Message}\n");
                TryDelete(zipPath);
                fail++;
                continue;
            }

            if (verify && !VerifyZip(zipPath, entryName, srcSize, out string verr))
            {
                cb.Write("fail", $"[FAIL]  {Path.GetFileName(fp)}: verify: {verr}\n");
                TryDelete(zipPath);
                fail++;
                continue;
            }

            try
            {
                File.Delete(fp);
                long sz = new FileInfo(zipPath).Length;
                cb.Write("ok", $"[OK]    {Path.GetFileName(fp)}  ({sz:N0} B)\n");
                ok++;
            }
            catch (Exception e)
            {
                cb.Write("warn", $"[WARN]  {Path.GetFileName(fp)}: packed OK, delete failed: {e.Message}\n");
                ok++;
            }
        }

        cb.Write("info", new string('─', 64) + "\n");
        cb.Write("info", $"Done.  OK: {ok}  Fail: {fail}  Skip: {skipped}  |  {ArchiveExtractor.FmtDuration(t0.Elapsed.TotalSeconds)}\n");
        cb.Stat?.Invoke($"Done — OK:{ok}  Fail:{fail}  Skip:{skipped}");
        cb.Progress?.Invoke(100);
    }

    private static bool VerifyZip(string zipPath, string entryName, long srcSize, out string error)
    {
        error = "";
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry(entryName);
            if (entry is null)
            {
                error = "entry missing after write";
                return false;
            }
            if (entry.Length != srcSize)
            {
                error = $"size mismatch ({entry.Length} vs {srcSize})";
                return false;
            }
            // Read the entry through to confirm it is not corrupt
            using var s = entry.Open();
            var buffer = new byte[81920];
            while (s.Read(buffer, 0, buffer.Length) > 0) { }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}

/// <summary>
/// Remove ReadOnly Attribute tool, ported from the suite: clears the read-only
/// flag recursively, then removes the Zone.Identifier NTFS stream via
/// PowerShell Unblock-File (may require elevation).
/// </summary>
public static class ReadOnlyRemover
{
    public static void Run(string target, ArchiveLog cb, CancellationToken stop)
    {
        cb.Write("dim", $"Target: {target}\n");
        cb.Write("dim", new string('─', 60) + "\n");

        // ── Step 1: clear read-only attribute ────────────────────────────
        cb.Write("info", "Step 1: Clearing read-only attribute...\n");
        var paths = new List<string>();
        if (File.Exists(target))
        {
            paths.Add(target);
        }
        else if (Directory.Exists(target))
        {
            cb.Write("dim", $"  Scanning: {target}\n");
            try
            {
                paths.AddRange(Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories));
                paths.AddRange(Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories));
            }
            catch (Exception e)
            {
                cb.Write("warn", $"  [WARN] scan incomplete: {e.Message}\n");
            }
            paths.Add(target);
        }

        int chmodOk = 0, chmodFail = 0;
        foreach (string p in paths)
        {
            try
            {
                var attrs = File.GetAttributes(p);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(p, attrs & ~FileAttributes.ReadOnly);
                chmodOk++;
            }
            catch (Exception e)
            {
                cb.Write("warn", $"  [WARN] attribute clear failed: {p}\n        {e.Message}\n");
                chmodFail++;
            }
        }
        cb.Write("ok", $"  Done — {chmodOk} path(s) updated"
            + (chmodFail > 0 ? $", {chmodFail} failed" : "") + "\n");

        // ── Step 2: remove Zone.Identifier via PowerShell Unblock-File ───
        if (stop.IsCancellationRequested)
        {
            cb.Write("warn", "\n[STOPPED — Step 2 skipped]\n");
            return;
        }
        cb.Write("info", "\nStep 2: Removing Zone.Identifier (Unblock-File)...\n");
        cb.Write("dim", "  Note: This step may silently require Administrator privileges.\n");

        string psCmd = File.Exists(target)
            ? $"Unblock-File -LiteralPath '{target}'"
            : $"Get-ChildItem -LiteralPath '{target}' -Recurse -File | Unblock-File";

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in (string[])["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", psCmd])
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(300_000))
            {
                try { p.Kill(); } catch { /* ignore */ }
                cb.Write("warn", "  Unblock-File timed out.\n");
            }
            else if (p.ExitCode == 0)
            {
                cb.Write("ok", "  Unblock-File completed successfully.\n");
            }
            else
            {
                cb.Write("warn", "  Unblock-File returned a non-zero exit code.\n");
                if (stderr.Trim().Length > 0)
                    cb.Write("warn", $"  stderr: {stderr.Trim()}\n");
                cb.Write("warn", "  If files remain blocked, try re-running as Administrator.\n");
            }
        }
        catch (Exception e)
        {
            cb.Write("err", $"  [ERROR] PowerShell failed: {e.Message}\n");
            cb.Write("warn", "  The attribute step completed, but Zone.Identifier removal was not performed.\n");
        }

        cb.Write("dim", new string('─', 60) + "\n");
        cb.Write("ok", "All operations complete.\n");
    }
}
