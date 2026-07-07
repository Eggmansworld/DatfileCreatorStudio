using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>State for the Recursive Archive Extractor window.</summary>
public partial class RecursiveArchiveExtractorViewModel : ArchiveToolViewModel
{
    public RecursiveArchiveExtractorViewModel(string sevenZipPath)
    {
        _sevenZipPath = sevenZipPath;
    }

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _sevenZipPath;

    // Destination
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomDestEnabled))]
    private bool _destSame = true;

    [ObservableProperty] private bool _destCustom;
    [ObservableProperty] private string _customDest = "";
    public bool CustomDestEnabled => DestCustom;

    // Formats
    [ObservableProperty] private bool _fmtZip = true;
    [ObservableProperty] private bool _fmt7z = true;
    [ObservableProperty] private bool _fmtRar = true;

    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private bool _autoNested;

    // After extraction: "keep" | "recycle" | "permanent" | "move_mirror" | "move_flat"
    [ObservableProperty] private bool _afterKeep = true;
    [ObservableProperty] private bool _afterRecycle;
    [ObservableProperty] private bool _afterPermanent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveDestVisible))]
    private bool _afterMoveMirror;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveDestVisible))]
    private bool _afterMoveFlat;

    [ObservableProperty] private string _moveDest = "";
    public bool MoveDestVisible => AfterMoveMirror || AfterMoveFlat;

    private string AfterMode =>
        AfterRecycle ? "recycle" : AfterPermanent ? "permanent"
        : AfterMoveMirror ? "move_mirror" : AfterMoveFlat ? "move_flat" : "keep";

    public async Task RunAsync()
    {
        string src = SourcePath.Trim();
        if (!File.Exists(SevenZipPath))
        {
            Post("fail", $"ERROR: 7z.exe not found at:\n{SevenZipPath}\n");
            return;
        }
        if (src.Length == 0 || !Directory.Exists(src))
        {
            Post("fail", "ERROR: Source folder not set or does not exist.\n");
            return;
        }

        string? customDst = null;
        if (DestCustom)
        {
            customDst = CustomDest.Trim();
            if (customDst.Length == 0)
            {
                Post("fail", "ERROR: Custom destination not set.\n");
                return;
            }
        }

        string afterMode = AfterMode;
        string? moveRoot = null;
        if (afterMode is "move_mirror" or "move_flat")
        {
            moveRoot = MoveDest.Trim();
            if (moveRoot.Length == 0)
            {
                Post("fail", "ERROR: Move destination not set.\n");
                return;
            }
        }

        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FmtZip) exts.Add(".zip");
        if (Fmt7z) exts.Add(".7z");
        if (FmtRar) exts.Add(".rar");
        if (exts.Count == 0)
        {
            Post("fail", "ERROR: No formats selected.\n");
            return;
        }

        var log = MakeLog();
        string sevenZip = SevenZipPath;
        bool recurse = Recursive, autoNested = AutoNested;
        await RunAsync(token => ArchiveExtractor.Extract(
            sevenZip, src, customDst, exts, afterMode, moveRoot, recurse, autoNested, log, token));
    }
}
