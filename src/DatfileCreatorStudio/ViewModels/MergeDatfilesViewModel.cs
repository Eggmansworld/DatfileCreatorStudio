using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>State for the Merge Datfiles window.</summary>
public partial class MergeDatfilesViewModel : ArchiveToolViewModel
{
    [ObservableProperty] private string _rootPath = "";

    public async Task RunAsync()
    {
        string root = RootPath.Trim().Trim('"');
        if (root.Length == 0 || !Directory.Exists(root))
        {
            Post("err", $"ERROR: Folder not found:\n{root}\n");
            return;
        }

        var log = MakeLog();
        await RunAsync(token => Merge(root, log, token));
    }

    // Direct port of the suite's MergeDatfilesWindow._worker
    private static void Merge(string root, ArchiveLog cb, CancellationToken stop)
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        string category = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));

        cb.Write("dim", new string('=', 60) + "\n");
        cb.Write("hdr", "Merge Datfiles\n");
        cb.Write("info", "Root     : " + root + "\n");
        cb.Write("info", "Category : " + category + "\n");
        cb.Write("info", "Date     : " + today + "\n");
        cb.Write("dim", new string('=', 60) + "\n");

        cb.Write("dim", "\nScanning...\n");
        var jobs = MergeDatfiles.ScanForMerge(root);
        if (jobs.Count == 0)
        {
            cb.Write("warn", "\nNo subfolders found under the selected root.\n");
            return;
        }

        var mergeJobs = jobs.Where(j => j.Action == "merge").ToList();

        cb.Write("hdr", "\nPlan:\n");
        foreach (var j in jobs)
        {
            bool isMerge = j.Action == "merge";
            cb.Write(isMerge ? "ok" : "dim",
                (isMerge ? "  ▶ " : "  – ") + j.Name + "  —  " + j.Reason + "\n");
        }

        if (mergeJobs.Count == 0)
        {
            cb.Write("warn", "\nNothing to merge.\n");
            return;
        }

        cb.Write("dim", "\n" + new string('─', 60) + "\n");

        int mergedCount = 0, errorCount = 0;
        foreach (var job in mergeJobs)
        {
            if (stop.IsCancellationRequested)
            {
                cb.Write("warn", "\nStopped by user.\n");
                break;
            }

            cb.Write("hdr", "\n[" + job.Name + "]\n");
            cb.Write("dim", "  Sources:\n");
            foreach (string dp in job.Deeper)
                cb.Write("dim", "    " + Path.GetRelativePath(root, dp) + "\n");

            var (merged, header, err) = MergeDatfiles.CollectGames(job.Path, job.Deeper);
            if (err.Length > 0)
            {
                cb.Write("err", "  ERROR: " + err + "\n");
                errorCount++;
                continue;
            }

            string datName = category + " - " + job.Name;
            string outFn = DatWriter.MakeDatFilename(datName, today);
            string outPath = Path.Combine(job.Path, outFn);

            var gameNames = merged.Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            int romTotal = merged.Sum(kv => kv.Value.Count);
            cb.Write("info", "  Games  : " + gameNames.Count + "  (" + string.Join(", ", gameNames) + ")\n");
            cb.Write("info", "  ROMs   : " + romTotal + "\n");
            cb.Write("info", "  Output : " + Path.GetRelativePath(root, outPath) + "\n");

            try
            {
                MergeDatfiles.WriteMergedDat(outPath, datName, merged, header, today);
                cb.Write("ok", "  ✔ Written\n");
                mergedCount++;
            }
            catch (Exception exc)
            {
                cb.Write("err", "  ERROR writing: " + exc.Message + "\n");
                errorCount++;
            }
        }

        cb.Write("dim", "\n" + new string('=', 60) + "\n");
        if (errorCount > 0)
            cb.Write("warn", $"Done — {mergedCount} merged, {errorCount} error(s).\n");
        else
            cb.Write("ok", $"Done — {mergedCount} dat(s) merged successfully.\n");
    }
}
