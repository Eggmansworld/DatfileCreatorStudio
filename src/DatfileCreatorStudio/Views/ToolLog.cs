using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DatfileCreatorStudio.Views;

/// <summary>Shared "Save Log" helper for the tool windows.</summary>
public static class ToolLog
{
    public static async Task SaveAsync(Window owner, IReadOnlyList<string>? lines, string baseName)
    {
        if (lines is not { Count: > 0 })
            return;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Log",
            SuggestedFileName = $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Text files") { Patterns = ["*.txt"] }],
        });
        if (file is null)
            return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n"));
        }
        catch
        {
            // best-effort
        }
    }
}
