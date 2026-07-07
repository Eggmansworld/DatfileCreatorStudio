using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>State for the ZIP Store Packer window.</summary>
public partial class ZipStorePackerViewModel : ArchiveToolViewModel
{
    public ObservableCollection<string> Extensions { get; } = [".exe"];

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _extEntry = "";
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private bool _verify = true;
    [ObservableProperty] private bool _skipExisting = true;

    /// <summary>Add one or more extensions (space/comma separated) from the entry box.</summary>
    public void AddExtensions()
    {
        foreach (string tok in Regex.Split(ExtEntry, @"[\s,]+"))
        {
            string t = tok.Trim().TrimStart('.');
            if (t.Length == 0)
                continue;
            string ext = "." + t.ToLowerInvariant();
            if (!Extensions.Contains(ext))
                Extensions.Add(ext);
        }
        ExtEntry = "";
    }

    public void RemoveExtension(string ext) => Extensions.Remove(ext);

    public async Task RunAsync()
    {
        string src = SourcePath.Trim();
        if (src.Length == 0 || !Directory.Exists(src))
        {
            Post("fail", "ERROR: Target folder not set or does not exist.\n");
            return;
        }
        if (Extensions.Count == 0)
        {
            Post("fail", "ERROR: No extensions configured.\n");
            return;
        }

        var exts = Extensions.ToList();
        var log = MakeLog();
        bool recurse = Recursive, verify = Verify, skip = SkipExisting;
        await RunAsync(token => ZipStorePacker.Pack(src, exts, recurse, verify, skip, log, token));
    }
}
