using System.Windows;
using System.Windows.Input;
using System.Text.RegularExpressions;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pianyu.Core;

namespace Pianyu.App.Views;

public sealed record ExplodedSnippetBlock(int Index, string Text);

public sealed class SnippetDetailViewModel
{
    public Snippet Snippet { get; }
    public IReadOnlyList<ExplodedSnippetBlock> Blocks { get; }
    public string BlockSummary => $"正文已按分隔符拆分为 {Blocks.Count} 个可复制块 · 点击任意块复制";

    public SnippetDetailViewModel(Snippet snippet)
    {
        Snippet = snippet;
        Blocks = Explode(snippet.Content);
    }

    private static IReadOnlyList<ExplodedSnippetBlock> Explode(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return [];

        var chunks = Regex.Split(normalized, @"\n\s*\n|(?m)^\s*(?:-{3,}|={3,}|\*{3,}|_{3,}|—{3,})\s*$")
            .SelectMany(chunk => Regex.Split(chunk, @"\n(?=\s*(?:#{1,6}\s|[-*+]\s|\d+[.)]\s|[一二三四五六七八九十]+[、.]\s))"))
            .Select(chunk => chunk.Trim())
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk))
            .ToList();

        return chunks.Select((text, index) => new ExplodedSnippetBlock(index + 1, text)).ToList();
    }
}

public partial class SnippetDetailWindow : Window
{
    private readonly AppServices _services;
    private readonly Snippet _snippet;

    public SnippetDetailWindow(AppServices services, Snippet snippet)
    {
        InitializeComponent();
        _services = services;
        _snippet = snippet;
        DataContext = new SnippetDetailViewModel(snippet);
    }

    private async void Block_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ExplodedSnippetBlock block }) return;
        if (!_services.Clipboard.SetText(block.Text)) return;
        try { await _services.Repository.RecordUseAsync(_snippet.Id, null, "copy-detail-block"); }
        catch { /* 统计失败不能阻塞块复制。 */ }
        e.Handled = true;
    }

    private async void Copy_OnClick(object sender, RoutedEventArgs e)
    {
        if (!_services.Clipboard.SetText(_snippet.Content)) return;
        try { await _services.Repository.RecordUseAsync(_snippet.Id, null, "copy-detail"); }
        catch { /* 统计失败不能阻塞复制。 */ }
        Close();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }
}
