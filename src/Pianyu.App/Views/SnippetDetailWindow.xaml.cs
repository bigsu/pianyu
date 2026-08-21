using System.Windows;
using System.Windows.Input;
using System.Text.RegularExpressions;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pianyu.App.Infrastructure;
using Pianyu.Core;

namespace Pianyu.App.Views;

public sealed record ExplodedSnippetBlock(int Index, string Text);

public sealed class ExplodedSnippetBlockViewModel : ObservableObject
{
    private bool _isSelected;
    private int _selectionOrder;

    public int Index { get; }
    public string Text { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public int SelectionOrder { get => _selectionOrder; set => SetProperty(ref _selectionOrder, value); }

    public ExplodedSnippetBlockViewModel(ExplodedSnippetBlock block)
    {
        Index = block.Index;
        Text = block.Text;
    }
}

public sealed class SnippetDetailViewModel : ObservableObject
{
    public Snippet Snippet { get; }
    public IReadOnlyList<ExplodedSnippetBlockViewModel> Blocks { get; }
    public bool HasSelectedBlocks => Blocks.Any(block => block.IsSelected);
    public string SelectionSummary => HasSelectedBlocks
        ? $"已选择 {Blocks.Count(block => block.IsSelected)} 个正文块 · 按点击顺序拼接复制"
        : $"正文已按分隔符拆分为 {Blocks.Count} 个块 · 点击块进行连续选择";

    public SnippetDetailViewModel(Snippet snippet)
    {
        Snippet = snippet;
        Blocks = Explode(snippet.Content).Select(block => new ExplodedSnippetBlockViewModel(block)).ToList();
    }

    public void ToggleBlock(ExplodedSnippetBlockViewModel block)
    {
        block.IsSelected = !block.IsSelected;
        ReindexSelection();
        OnPropertyChanged(nameof(HasSelectedBlocks));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public string GetSelectedText() => string.Join(Environment.NewLine + Environment.NewLine,
        Blocks.Where(block => block.IsSelected).OrderBy(block => block.SelectionOrder).Select(block => block.Text));

    private void ReindexSelection()
    {
        var order = 1;
        foreach (var block in Blocks.Where(block => block.IsSelected).OrderBy(block => block.SelectionOrder == 0 ? int.MaxValue : block.SelectionOrder).ThenBy(block => block.Index))
            block.SelectionOrder = order++;
        foreach (var block in Blocks.Where(block => !block.IsSelected)) block.SelectionOrder = 0;
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

    private void Block_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ExplodedSnippetBlockViewModel block }) return;
        if (DataContext is SnippetDetailViewModel viewModel) viewModel.ToggleBlock(block);
        e.Handled = true;
    }

    private async void CopySelected_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SnippetDetailViewModel viewModel || !viewModel.HasSelectedBlocks) return;
        if (!_services.Clipboard.SetText(viewModel.GetSelectedText())) return;
        try { await _services.Repository.RecordUseAsync(_snippet.Id, null, "copy-detail-blocks"); }
        catch { /* 统计失败不能阻塞块复制。 */ }
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
