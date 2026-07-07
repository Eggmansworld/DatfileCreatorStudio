using CommunityToolkit.Mvvm.ComponentModel;
using DatfileCreator.Core;

namespace DatfileCreatorStudio.ViewModels;

/// <summary>State for the Remove ReadOnly Attribute window.</summary>
public partial class RemoveReadOnlyViewModel : ArchiveToolViewModel
{
    [ObservableProperty] private string _targetPath = "";

    public async Task RunAsync()
    {
        string target = TargetPath.Trim().Trim('"');
        if (target.Length == 0)
        {
            Post("err", "ERROR: Please select a file or folder first.\n");
            return;
        }
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            Post("err", $"ERROR: Path does not exist:\n{target}\n");
            return;
        }

        var log = MakeLog();
        await RunAsync(token => ReadOnlyRemover.Run(target, log, token));
    }
}
