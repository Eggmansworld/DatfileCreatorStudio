using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>
/// State for the Validate Datfiles tool: scans every rom entry in the given
/// dat file or folder of dats and reports attribute anomalies.
/// </summary>
public partial class ValidateDatfilesViewModel : ViewModelBase
{
    private static readonly IBrush HdrBrush = new SolidColorBrush(Color.Parse("#4A9EDA"));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#3FB950"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IBrush ErrBrush = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush IssueBrush = new SolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#8A8F98"));

    private CancellationTokenSource? _cancel;
    private readonly BatchedLog _log = new();

    public ObservableCollection<LogLine> Lines => _log.Lines;

    [ObservableProperty] private string _targetPath = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "Select a dat file or folder, then click Validate.";

    public IReadOnlyList<string> LogLinesSnapshot => _log.Snapshot;

    private void Post(string text, IBrush brush) => _log.Post(text, brush);

    public void Stop() => _cancel?.Cancel();

    public void ClearLog() => _log.Clear();

    public async Task RunAsync()
    {
        string path = TargetPath.Trim().Trim('"');
        bool isFile = File.Exists(path);
        if (path.Length == 0 || (!isFile && !Directory.Exists(path)))
        {
            Post("Select an existing dat file or folder first.", ErrBrush);
            return;
        }

        var files = DatValidator.CollectFiles(path, singleMode: isFile);
        if (files.Count == 0)
        {
            Post("No .dat/.xml files found at that location.", WarnBrush);
            return;
        }

        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        IsRunning = true;
        StatusText = "Validating...";

        try
        {
            await Task.Run(() =>
            {
                int totalFiles = files.Count;
                int totalIssues = 0;
                int totalRoms = 0;

                Post(new string('=', 72), DimBrush);
                Post("Validate Datfiles", HdrBrush);
                Post("Files to scan  : " + totalFiles, DimBrush);
                Post(new string('=', 72), DimBrush);

                for (int i = 0; i < files.Count; i++)
                {
                    if (token.IsCancellationRequested)
                    {
                        Post("", DimBrush);
                        Post("Stopped by user.", WarnBrush);
                        break;
                    }
                    string fp = files[i];
                    Post("", DimBrush);
                    Post($"[{i + 1}/{totalFiles}]  {fp}", HdrBrush);

                    try
                    {
                        var (issues, roms) = DatValidator.ValidateFile(
                            fp, line => Post(line, IssueBrush), () => token.IsCancellationRequested);
                        totalIssues += issues;
                        totalRoms += roms;

                        if (roms == 0)
                            Post("  (no <rom> entries found in this file)", WarnBrush);
                        else if (issues == 0)
                            Post($"  ✔  No issues found  ({roms} ROM entr{(roms == 1 ? "y" : "ies")} checked)",
                                 OkBrush);
                        else
                            Post($"  ✖  {issues} issue{(issues != 1 ? "s" : "")} found in "
                                 + $"{roms} ROM entr{(roms == 1 ? "y" : "ies")}", WarnBrush);
                    }
                    catch (Exception exc)
                    {
                        Post("  [ERROR] Cannot read file: " + exc.Message, ErrBrush);
                        totalIssues++;
                    }
                }

                Post("", DimBrush);
                Post(new string('=', 72), DimBrush);
                string summary = $"Done.  Files: {totalFiles}  |  ROM entries checked: {totalRoms}"
                    + $"  |  Issues found: {totalIssues}";
                Post(summary, totalIssues == 0 && !token.IsCancellationRequested ? OkBrush : WarnBrush);

                Dispatcher.UIThread.Post(() =>
                    StatusText = $"Done — {totalIssues} issue(s) across {totalFiles} file(s)");
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A tool bug must never crash the whole app
            Post("[ERROR] Validation failed: " + ex.Message, ErrBrush);
            StatusText = "Validation failed.";
        }
        finally
        {
            IsRunning = false;
            _cancel = null;
        }
    }
}
