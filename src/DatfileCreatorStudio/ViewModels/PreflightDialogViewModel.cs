using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

public enum PreflightDecision { Cancel, Proceed, FullRehash }

/// <summary>What the user chose in the Pre-flight Check dialog.</summary>
public sealed record PreflightOutcome(PreflightDecision Decision, string NewVersion);

/// <summary>
/// State for the Incremental Update Pre-flight Check dialog: runs the
/// validation on a background task, streams colour-coded result lines, and
/// holds the full inspection log for saving.
/// </summary>
public partial class PreflightDialogViewModel : ViewModelBase
{
    private static readonly Dictionary<PreflightTag, IBrush> TagBrushes = new()
    {
        [PreflightTag.Plain] = new SolidColorBrush(Color.Parse("#8A8F98")),
        [PreflightTag.Ok] = new SolidColorBrush(Color.Parse("#3FB950")),
        [PreflightTag.Warn] = new SolidColorBrush(Color.Parse("#E8A33D")),
        [PreflightTag.Err] = new SolidColorBrush(Color.Parse("#E5484D")),
        [PreflightTag.Dim] = new SolidColorBrush(Color.Parse("#7A7F87")),
    };

    private readonly DatSettings _settings;
    private CancellationTokenSource? _stopScan;

    public ObservableCollection<LogLine> Lines { get; } = [];

    /// <summary>Full anomaly detail from the last completed scan.</summary>
    public List<string> InspectionLog { get; private set; } = [];

    public PreflightDialogViewModel(DatSettings settings)
    {
        _settings = settings;
        _newVersion = settings.Version;
        DatSourceText = settings.IncrementalDatPath;
        InputRootText = settings.InputRoot;
        DatTypeText = settings.IsMixed ? "Mixed (Archive as File)" : "Zipped";
        IsMixed = settings.IsMixed;
        _ = RunValidationAsync();
    }

    public string DatSourceText { get; }
    public string InputRootText { get; }
    public string DatTypeText { get; }
    public bool IsMixed { get; }

    [ObservableProperty] private string _newVersion;
    [ObservableProperty] private bool _canProceed;
    [ObservableProperty] private bool _canStop;
    [ObservableProperty] private bool _canSaveLog;
    [ObservableProperty] private bool _isScanning;

    public async Task RunValidationAsync()
    {
        _stopScan?.Cancel();
        _stopScan = new CancellationTokenSource();
        var token = _stopScan.Token;

        Lines.Clear();
        InspectionLog = [];
        CanProceed = false;
        CanSaveLog = false;
        CanStop = true;
        IsScanning = true;

        void Post(string text, PreflightTag tag) =>
            Dispatcher.UIThread.Post(() => Lines.Add(new LogLine(text, TagBrushes[tag])));

        PreflightResult? result = null;
        try
        {
            result = await Task.Run(() => PreflightCheck.Run(_settings, Post, token));
        }
        catch (Exception ex)
        {
            Post("Validation crashed: " + ex.Message, PreflightTag.Err);
        }

        IsScanning = false;
        CanStop = false;
        if (result is not null)
        {
            InspectionLog = result.InspectionLog;
            CanSaveLog = InspectionLog.Count > 0;
            // Proceed is enabled only after a scan that ran to completion
            // (warnings included) — a stopped or failed scan keeps it
            // disabled until a rescan finishes, same as the suite
            CanProceed = !result.Failed && !result.Stopped;
        }
    }

    public void StopScan()
    {
        _stopScan?.Cancel();
        CanStop = false;
    }
}
