using System.ComponentModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using DatfileCreatorStudio.ViewModels;

namespace DatfileCreatorStudio.Views;

public partial class PreviewWindow : Window
{
    // Above this size, skip syntax highlighting and show plain text — tens of
    // thousands of inline runs make the text layout crawl on very large dats
    private const int HighlightLimit = 1_000_000;

    private static readonly IBrush PunctBrush = new ImmutableSolidColorBrush(Color.Parse("#7A7F87"));
    private static readonly IBrush TagBrush = new ImmutableSolidColorBrush(Color.Parse("#4A9EDA"));
    private static readonly IBrush AttrNameBrush = new ImmutableSolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IBrush AttrValueBrush = new ImmutableSolidColorBrush(Color.Parse("#3FB950"));

    private PreviewWindowViewModel? _viewModel;

    public PreviewWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = DataContext as PreviewWindowViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                RenderXml(_viewModel.XmlText);
            }
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PreviewWindowViewModel.XmlText) && _viewModel is not null)
            RenderXml(_viewModel.XmlText);
    }

    // ── XML syntax highlighting ──────────────────────────────────────────

    private void RenderXml(string xml)
    {
        var inlines = XmlView.Inlines ??= [];
        inlines.Clear();

        if (xml.Length == 0)
            return;
        if (xml.Length > HighlightLimit)
        {
            inlines.Add(new Run(xml));
            return;
        }

        int i = 0;
        while (i < xml.Length)
        {
            int lt = xml.IndexOf('<', i);
            if (lt < 0)
            {
                AddRun(inlines, xml[i..], null); // trailing text content
                break;
            }
            if (lt > i)
                AddRun(inlines, xml[i..lt], null); // text content

            int gt = xml.IndexOf('>', lt);
            if (gt < 0)
            {
                AddRun(inlines, xml[lt..], PunctBrush);
                break;
            }
            HighlightTag(inlines, xml.AsSpan(lt, gt - lt + 1));
            i = gt + 1;
        }
    }

    /// <summary>Emit coloured runs for one &lt;...&gt; tag.</summary>
    private static void HighlightTag(InlineCollection inlines, ReadOnlySpan<char> tag)
    {
        // Leading punctuation: "<", "</", "<?"
        int p = 1;
        while (p < tag.Length && (tag[p] == '/' || tag[p] == '?' || tag[p] == '!'))
            p++;
        AddRun(inlines, tag[..p].ToString(), PunctBrush);

        // Tag name
        int nameEnd = p;
        while (nameEnd < tag.Length && !char.IsWhiteSpace(tag[nameEnd])
               && tag[nameEnd] is not ('>' or '/' or '?'))
            nameEnd++;
        if (nameEnd > p)
            AddRun(inlines, tag[p..nameEnd].ToString(), TagBrush);

        // Attributes: name, =, "value"
        int j = nameEnd;
        while (j < tag.Length)
        {
            char c = tag[j];
            if (c == '"')
            {
                int close = tag[(j + 1)..].IndexOf('"');
                int end = close < 0 ? tag.Length : j + 1 + close + 1;
                AddRun(inlines, tag[j..end].ToString(), AttrValueBrush);
                j = end;
            }
            else if (char.IsWhiteSpace(c) || c is '=' or '/' or '?' or '>')
            {
                int start = j;
                while (j < tag.Length && (char.IsWhiteSpace(tag[j]) || tag[j] is '=' or '/' or '?' or '>'))
                    j++;
                AddRun(inlines, tag[start..j].ToString(), PunctBrush);
            }
            else
            {
                int start = j;
                while (j < tag.Length && !char.IsWhiteSpace(tag[j]) && tag[j] is not ('=' or '"' or '>' or '/'))
                    j++;
                AddRun(inlines, tag[start..j].ToString(), AttrNameBrush);
            }
        }
    }

    private static void AddRun(InlineCollection inlines, string text, IBrush? brush)
    {
        if (text.Length == 0)
            return;
        var run = new Run(text);
        if (brush is not null)
            run.Foreground = brush;
        inlines.Add(run);
    }

    // ── Buttons ──────────────────────────────────────────────────────────

    private async void OnCopyAllClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is { XmlText.Length: > 0 } vm && Clipboard is not null)
            await Clipboard.SetTextAsync(vm.XmlText);
    }

    private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { Current: not null } vm)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Chosen Dat Structure As...",
            SuggestedFileName = vm.SuggestedFileName,
            DefaultExtension = "xml",
            FileTypeChoices =
            [
                new FilePickerFileType("XML dat files") { Patterns = ["*.xml"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        if (file is null)
            return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(vm.XmlText));
        }
        catch
        {
            // Save is best-effort; the XML remains available for copy
        }
    }
}
