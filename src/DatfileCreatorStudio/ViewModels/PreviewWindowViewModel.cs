using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>
/// State for the Dat Preview window: which completed dat is selected, which
/// structure option to render it with, and the resulting XML. Switching either
/// re-renders instantly from in-memory hash data — no re-hashing.
/// </summary>
public partial class PreviewWindowViewModel : ViewModelBase
{
    // Same labels as the suite's preview window (its own numbering, which
    // intentionally differs from the main window's README-style numbering)
    private static readonly (string Key, string Label)[] StructOpts =
    [
        ("opt1", "1 — Dirs"),
        ("opt2", "2 — Archives as Games"),
        ("opt3", "3 — First Level Dirs as Games"),
        ("opt4", "4 — First Level Dirs as Games + Merge Dirs in Games"),
    ];

    private readonly List<PreviewEntry> _entries;

    public ObservableCollection<string> DatNames { get; } = [];

    public PreviewWindowViewModel(List<PreviewEntry> entries)
    {
        _entries = entries;
        foreach (var e in entries)
            DatNames.Add(e.DatName);

        // Start on the structure the run actually used
        string initial = entries.Count > 0 ? entries[0].Settings.Structure : "opt2";
        _structOpt1 = initial == "opt1";
        _structOpt2 = initial == "opt2";
        _structOpt3 = initial == "opt3";
        _structOpt4 = initial == "opt4";
        _selectedIndex = entries.Count > 0 ? 0 : -1;
        Rerender();
    }

    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private bool _structOpt1;
    [ObservableProperty] private bool _structOpt2;
    [ObservableProperty] private bool _structOpt3;
    [ObservableProperty] private bool _structOpt4;

    [ObservableProperty] private string _xmlText = "";
    [ObservableProperty] private string _infoText = "";

    partial void OnSelectedIndexChanged(int value) => Rerender();
    partial void OnStructOpt1Changed(bool value) { if (value) Rerender(); }
    partial void OnStructOpt2Changed(bool value) { if (value) Rerender(); }
    partial void OnStructOpt3Changed(bool value) { if (value) Rerender(); }
    partial void OnStructOpt4Changed(bool value) { if (value) Rerender(); }

    public PreviewEntry? Current =>
        SelectedIndex >= 0 && SelectedIndex < _entries.Count ? _entries[SelectedIndex] : null;

    public string CurrentStructure =>
        StructOpt1 ? "opt1" : StructOpt3 ? "opt3" : StructOpt4 ? "opt4" : "opt2";

    public string CurrentStructureLabel =>
        StructOpts.First(o => o.Key == CurrentStructure).Label;

    /// <summary>Default filename for Save Chosen Dat Structure As (suite format).</summary>
    public string SuggestedFileName =>
        Current is { } e
            ? $"{DatfileCreator.Core.XmlText.SafeFilename(e.DatName)} ({e.HeaderDate}_RomVault) [{CurrentStructureLabel}].xml"
            : "preview.xml";

    private void Rerender()
    {
        if (Current is not { } entry)
        {
            XmlText = "";
            InfoText = "No completed dats to preview.";
            return;
        }
        string xml = PreviewRenderer.Render(entry, CurrentStructure);
        int romCount = xml.Split("<rom ").Length - 1;
        XmlText = xml;
        InfoText = $"{entry.DatName} — {CurrentStructureLabel} — {romCount} rom entr"
                   + (romCount == 1 ? "y" : "ies")
                   + (entry.IsTree ? "" : "  (flat per-folder dat: all structures render alike)");
    }
}
