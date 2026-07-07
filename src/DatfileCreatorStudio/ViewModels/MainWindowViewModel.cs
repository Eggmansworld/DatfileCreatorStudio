using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DatfileCreator.Core;
using DatfileCreatorStudio.Services;

namespace DatfileCreatorStudio.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private CancellationTokenSource? _softStop;
    private CancellationTokenSource? _hardStop;

    public LogDrawerViewModel Drawer { get; } = new();

    public MainWindowViewModel(SettingsService settings)
    {
        _settings = settings;
        var d = settings.Config.Dat;

        // Paths & header
        _inputRoot = d.InputRoot;
        _outputRoot = d.OutputRoot;
        _parentName = d.ParentName;
        _description = d.Description;
        _category = d.Category;
        _version = d.Version;
        _dateOverride = "";
        _author = d.Author;
        _url = d.Url;
        _homepage = d.Homepage;
        _comment = d.Comment;

        // Options
        _datTypeMixed = d.DatType == "mixed";
        _datTypeZipped = !_datTypeMixed;
        _genPerTop = d.GenMode == "per_top";
        _genPerRoot = d.GenMode == "per_root";
        _genPerAll = d.GenMode == "per_all";
        if (!_genPerTop && !_genPerRoot && !_genPerAll)
            _genPerRoot = true;
        _structOpt1 = d.Structure == "opt1";
        _structOpt2 = d.Structure == "opt2";
        _structOpt3 = d.Structure == "opt3";
        _structOpt4 = d.Structure == "opt4";
        if (!_structOpt1 && !_structOpt2 && !_structOpt3 && !_structOpt4)
            _structOpt2 = true;
        _formatModern = d.DatFormat != "legacy";
        _formatLegacy = !_formatModern;
        _useMachine = d.UseMachine;
        _inclGameDesc = d.InclGameDesc;
        _forcePacking = d.ForcePacking;
        _inclFileDate = d.InclFileDate;
        _includeMd5 = d.IncludeMd5;
        _includeSha256 = d.IncludeSha256;
        _includeBlake3 = d.IncludeBlake3;
        _extInclude = d.ExtInclude;
        _extExclude = d.ExtExclude;
        _multithread = d.Multithread;
        _threads = Math.Clamp(d.Threads, 1, 8);
        _netCapText = d.NetCapMbps > 0 ? d.NetCapMbps.ToString() : "0";
        _incremental = d.Incremental;
        _incrementalDatPath = d.IncrementalDatPath;
        _retireOldDats = d.RetireOldDats;
        _selectedTheme = settings.Config.Theme;

        Drawer.ReportInfo($"Config: {SettingsService.ConfigPath}");
    }

    // ── Paths & header fields ────────────────────────────────────────────

    [ObservableProperty] private string _inputRoot;
    [ObservableProperty] private string _outputRoot;
    [ObservableProperty] private string _parentName;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _category;
    [ObservableProperty] private string _version;
    [ObservableProperty] private string _dateOverride;
    [ObservableProperty] private string _author;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _homepage;
    [ObservableProperty] private string _comment;

    // ── Dat type ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMixed), nameof(IsZipped))]
    private bool _datTypeMixed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMixed), nameof(IsZipped))]
    private bool _datTypeZipped;

    public bool IsMixed => DatTypeMixed;
    public bool IsZipped => DatTypeZipped;

    // ── Generation mode ──────────────────────────────────────────────────

    [ObservableProperty] private bool _genPerTop;
    [ObservableProperty] private bool _genPerRoot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStructureEnabled))]
    private bool _genPerAll;

    /// <summary>Structure applies to the two recursive modes; per_all dats are flat by definition.</summary>
    public bool IsStructureEnabled => !GenPerAll;

    // ── Structure (README numbering: 1=opt2, 2=opt3, 3=opt4, 4=opt1) ─────

    [ObservableProperty] private bool _structOpt1;
    [ObservableProperty] private bool _structOpt2;
    [ObservableProperty] private bool _structOpt3;
    [ObservableProperty] private bool _structOpt4;

    // ── Format ───────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModern))]
    private bool _formatModern;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModern))]
    private bool _formatLegacy;

    public bool IsModern => FormatModern;

    [ObservableProperty] private bool _useMachine;
    [ObservableProperty] private bool _inclGameDesc;
    [ObservableProperty] private bool _forcePacking;
    [ObservableProperty] private bool _inclFileDate;

    // ── Hashes, filters, performance ─────────────────────────────────────

    [ObservableProperty] private bool _includeMd5;
    [ObservableProperty] private bool _includeBlake3;
    [ObservableProperty] private string _extInclude;
    [ObservableProperty] private string _extExclude;
    [ObservableProperty] private bool _multithread;
    [ObservableProperty] private int _threads;

    public int[] ThreadChoices { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Net cap in Mbit/s as text; "0" or invalid = auto (85% of NIC speed).</summary>
    [ObservableProperty] private string _netCapText;

    // ── Incremental update ───────────────────────────────────────────────

    [ObservableProperty] private bool _incremental;
    [ObservableProperty] private string _incrementalDatPath;
    [ObservableProperty] private bool _retireOldDats;

    /// <summary>
    /// Set by the view: shows the Pre-flight Check dialog and returns the
    /// user's decision (null = cancelled).
    /// </summary>
    public Func<DatSettings, Task<PreflightOutcome?>>? PreflightHandler { get; set; }

    // ── Preview ──────────────────────────────────────────────────────────

    /// <summary>Completed dats from the most recent run, for the preview window.</summary>
    public List<PreviewEntry> PreviewEntries { get; private set; } = [];

    [ObservableProperty] private bool _hasPreview;

    [ObservableProperty] private bool _includeSha256;

    partial void OnIncludeSha256Changed(bool value)
    {
        if (value)
            Drawer.ReportInfo(
                "Note: SHA-256 is informational only — RomVault displays it but does not use it "
                + "for matching. It adds hashing time without practical benefit (see README).");
    }

    // ── Run state ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(SoftStopCommand), nameof(HardStopCommand))]
    private bool _isRunning;

    public bool CanStart => !IsRunning;

    // ── Theme ────────────────────────────────────────────────────────────

    public string[] ThemeChoices { get; } = ["System", "Light", "Dark"];

    [ObservableProperty] private string _selectedTheme;

    partial void OnSelectedThemeChanged(string value)
    {
        ApplyTheme(value);
        _settings.Config.Theme = value;
        _settings.Save();
    }

    public static void ApplyTheme(string theme)
    {
        if (Application.Current is not { } app)
            return;
        app.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    // ── Settings capture ─────────────────────────────────────────────────

    private DatSettings BuildSettings()
    {
        var d = _settings.Config.Dat;
        d.InputRoot = InputRoot.Trim();
        d.OutputRoot = OutputRoot.Trim();
        d.ParentName = ParentName;
        d.Description = Description;
        d.Category = Category;
        d.Version = Version;
        d.Date = DateOverride.Trim();
        d.Author = Author;
        d.Url = Url;
        d.Homepage = Homepage;
        d.Comment = Comment;
        d.DatType = DatTypeMixed ? "mixed" : "zipped";
        d.GenMode = GenPerTop ? "per_top" : GenPerAll ? "per_all" : "per_root";
        d.Structure = StructOpt1 ? "opt1" : StructOpt3 ? "opt3" : StructOpt4 ? "opt4" : "opt2";
        d.DatFormat = FormatLegacy ? "legacy" : "modern";
        d.UseMachine = UseMachine;
        d.InclGameDesc = InclGameDesc;
        d.ForcePacking = ForcePacking;
        d.InclFileDate = InclFileDate;
        d.IncludeMd5 = IncludeMd5;
        d.IncludeSha256 = IncludeSha256;
        d.IncludeBlake3 = IncludeBlake3;
        d.ExtInclude = ExtInclude;
        d.ExtExclude = ExtExclude;
        d.Multithread = Multithread;
        d.Threads = Math.Clamp(Threads, 1, 8);
        d.NetCapMbps = int.TryParse(NetCapText.Trim(), out int cap) && cap > 0 ? cap : 0;
        d.Incremental = Incremental;
        d.IncrementalDatPath = IncrementalDatPath.Trim();
        d.RetireOldDats = RetireOldDats;
        return d;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        BuildSettings();
        _settings.Save();
        Drawer.ReportInfo("Settings saved to " + SettingsService.ConfigPath);
    }

    // ── Run control ──────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var s = BuildSettings();

        if (s.InputRoot.Length == 0 || !Directory.Exists(s.InputRoot))
        {
            Drawer.ReportError("Input top-level folder does not exist: '" + s.InputRoot + "'");
            return;
        }
        if (s.OutputRoot.Length == 0)
        {
            Drawer.ReportError("Please set an output folder.");
            return;
        }

        // Settings are written automatically when Start is pressed (suite behaviour)
        _settings.Save();

        // Run on a snapshot so mid-run UI edits can't affect the engine
        var runSettings = s.Clone();

        // Incremental mode: pre-flight check before anything starts
        if (runSettings.Incremental)
        {
            if (runSettings.IncrementalDatPath.Length == 0)
            {
                Drawer.ReportError("Incremental update is enabled but no existing dat file or folder is set.");
                return;
            }
            if (PreflightHandler is not null)
            {
                var outcome = await PreflightHandler(runSettings);
                if (outcome is null)
                    return; // user cancelled
                if (outcome.NewVersion.Length > 0)
                {
                    runSettings.Version = outcome.NewVersion;
                    Version = outcome.NewVersion;
                }
                if (outcome.Decision == PreflightDecision.FullRehash)
                {
                    runSettings.Incremental = false;
                    Drawer.ReportInfo("Full rehash mode (incremental disabled for this run).");
                }
                else
                {
                    Drawer.ReportInfo("Incremental update started.");
                }
            }
        }

        _softStop = new CancellationTokenSource();
        _hardStop = new CancellationTokenSource();
        IsRunning = true;
        Drawer.IsRunning = true;
        HasPreview = false;
        var previews = new List<PreviewEntry>();

        string summary = $"{(runSettings.IsMixed ? "Mixed" : "Zipped")} | {runSettings.GenMode} | "
                       + $"{runSettings.Structure} | {runSettings.DatFormat}"
                       + (runSettings.Incremental ? " | incremental" : "")
                       + $" | in: {runSettings.InputRoot} | out: {runSettings.OutputRoot}";
        Drawer.OnRunStarted(summary);

        bool ok = false;
        int errorCount = 0;
        bool stopped = false;

        var callbacks = new EngineCallbacks
        {
            Scan = (path, depth) =>
                Drawer.Append(LogKind.Phase, new string(' ', Math.Min(depth, 12) * 2) + "[scan] " + path),
            Status = msg => Drawer.Append(LogKind.Phase, msg),
            Totals = (jobs, items) =>
                Dispatcher.UIThread.Post(() => Drawer.OnTotals(jobs, items)),
            Folder = (path, n) =>
                Drawer.Append(LogKind.Folder, $">> {path}  ({n} items)"),
            Subfolder = rel =>
            {
                if (rel is not ("." or ""))
                    Drawer.Append(LogKind.Subfolder, "   [dir] " + rel);
            },
            Progress = done =>
                Dispatcher.UIThread.Post(() => Drawer.OnProgress(done)),
            ItemHashed = (name, detail) =>
                Drawer.Append(LogKind.Success, "   ✓ " + name + detail),
            ItemCarried = name =>
                Drawer.Append(LogKind.Carried, "   · " + name + "  (carried)"),
            ItemError = (name, detail) =>
                Drawer.Append(LogKind.Error, "   [ERROR] " + name + " :: " + detail),
            DatWritten = (path, n) =>
                Drawer.Append(LogKind.DatDone, "★ Dat written: " + path),
            Done = (isOk, errors, done, total, dats, elapsed) =>
            {
                ok = isOk;
                errorCount = errors.Count;
                foreach (string e in errors)
                    Drawer.Append(LogKind.Error, "[SUMMARY] " + e);
                Drawer.Append(LogKind.Phase,
                    $"[SUMMARY] {done}/{total} item(s), {dats} dat(s) written, {errors.Count} error(s).");
            },
        };

        try
        {
            var soft = _softStop.Token;
            var hard = _hardStop.Token;
            await Task.Run(() => DatEngine.Run(runSettings, callbacks, soft, hard, previews));
            stopped = _softStop.IsCancellationRequested || _hardStop.IsCancellationRequested;
        }
        catch (Exception ex)
        {
            Drawer.ReportError("Engine crashed: " + ex.Message);
        }
        finally
        {
            IsRunning = false;
            Drawer.IsRunning = false;
            PreviewEntries = previews;
            HasPreview = previews.Count > 0;
            if (HasPreview)
                Drawer.ReportInfo($"{previews.Count} dat(s) available in the preview window.");
            Drawer.OnRunCompleted(ok, errorCount, stopped);
            _softStop?.Dispose();
            _hardStop?.Dispose();
            _softStop = null;
            _hardStop = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void SoftStop()
    {
        _softStop?.Cancel();
        Drawer.ReportInfo("Soft stop requested — finishing the current folder, then writing its dat...");
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void HardStop()
    {
        _hardStop?.Cancel();
        _softStop?.Cancel();
        Drawer.ReportInfo("HARD stop requested — abandoning work as soon as possible...");
    }
}
