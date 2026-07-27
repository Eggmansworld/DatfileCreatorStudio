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
    // Names match the main window's Structure radios exactly — the suite used
    // its own numbering here, so the preview named a structure the user could
    // not find on the main window.
    private static readonly (string Key, string Label)[] StructOpts =
    [
        ("opt2", "Standard"),
        ("opt3", "Grouped"),
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
        _structOpt3 = initial == "opt3";
        _structOpt2 = !_structOpt3;
        _selectedIndex = entries.Count > 0 ? 0 : -1;
        Rerender();
    }

    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private bool _structOpt2;
    [ObservableProperty] private bool _structOpt3;

    [ObservableProperty] private string _xmlText = "";
    [ObservableProperty] private string _infoText = "";

    partial void OnSelectedIndexChanged(int value) => Rerender();
    partial void OnStructOpt2Changed(bool value) { if (value) Rerender(); }
    partial void OnStructOpt3Changed(bool value) { if (value) Rerender(); }

    public PreviewEntry? Current =>
        SelectedIndex >= 0 && SelectedIndex < _entries.Count ? _entries[SelectedIndex] : null;

    /// <summary>
    /// The folder-based structures are Mixed-only, so they are not offered for a
    /// Zipped dat — comparing against a shape that could never be written would
    /// only mislead.
    /// </summary>
    public bool AreFolderStructuresAvailable => Current?.Settings.IsMixed ?? true;

    public string CurrentStructure => StructOpt3 ? "opt3" : "opt2";

    public string CurrentStructureLabel =>
        StructOpts.First(o => o.Key == CurrentStructure).Label;

    /// <summary>Default filename for Save Chosen Dat Structure As (suite format).</summary>
    public string SuggestedFileName =>
        Current is { } e
            ? $"{DatfileCreator.Core.XmlText.SafeFilename(e.DatName)} ({e.HeaderDate}_RomVault) [{CurrentStructureLabel}].xml"
            : "preview.xml";

    private void Rerender()
    {
        OnPropertyChanged(nameof(AreFolderStructuresAvailable));
        if (Current is not { } entry)
        {
            XmlText = "";
            InfoText = "No completed dats to preview.";
            return;
        }
        // A Zipped dat has only one valid shape; snap back rather than render
        // something the writer would never produce.
        if (!entry.Settings.IsMixed && StructOpt3)
        {
            StructOpt3 = false;
            StructOpt2 = true;
            return; // the StructOpt2 change re-enters Rerender
        }
        string xml = PreviewRenderer.Render(entry, CurrentStructure);
        int romCount = xml.Split("<rom ").Length - 1;
        XmlText = xml;
        InfoText = $"{entry.DatName} — {CurrentStructureLabel} — {romCount} rom entr"
                   + (romCount == 1 ? "y" : "ies")
                   + (entry.IsTree ? "" : "  (flat per-folder dat: all structures render alike)");
    }
}
