using System.Windows;
using System.Windows.Input;
using System.Text;
using System.Globalization;
using System.Windows.Media;
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

    public string GetSelectedText() => string.Join(" ",
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

        var chunks = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length == 0) return;
            chunks.Add(current.ToString());
            current.Clear();
        }

        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                Flush();
                continue;
            }
            if (character is ',' or '，')
            {
                Flush();
                chunks.Add(character.ToString());
                continue;
            }
            current.Append(character);
        }
        Flush();

        return chunks.Select((text, index) => new ExplodedSnippetBlock(index + 1, text)).ToList();
    }
}

public sealed class ExplodedTextView : FrameworkElement
{
    private const double TokenFontSize = 16;
    private const double HorizontalPadding = 12;
    private const double VerticalPadding = 8;
    private const double TokenGap = 8;
    private readonly List<TokenLayout> _layouts = [];
    private double _layoutWidth = -1;

    public static readonly DependencyProperty BlocksProperty = DependencyProperty.Register(
        nameof(Blocks), typeof(IReadOnlyList<ExplodedSnippetBlockViewModel>), typeof(ExplodedTextView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ExplodedSnippetBlockViewModel>? Blocks
    {
        get => (IReadOnlyList<ExplodedSnippetBlockViewModel>?)GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    public ExplodedTextView()
    {
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Math.Max(ActualWidth, 640);
        BuildLayout(Math.Max(1, width));
        var height = _layouts.Count == 0 ? 0 : _layouts.Max(layout => layout.Bounds.Bottom);
        return new Size(width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Math.Abs(_layoutWidth - ActualWidth) > 0.5) BuildLayout(Math.Max(1, ActualWidth));

        var normalBackground = FindBrush("RaisedBrush", Brushes.DimGray);
        var normalBorder = FindBrush("BorderBrush", Brushes.Gray);
        var selectedBackground = FindBrush("AccentDarkBrush", Brushes.DarkSlateGray);
        var selectedBorder = FindBrush("AccentBrush", Brushes.Cyan);
        var textBrush = FindBrush("TextPrimaryBrush", Brushes.White);

        foreach (var layout in _layouts)
        {
            var selected = layout.Block.IsSelected;
            drawingContext.DrawRoundedRectangle(selected ? selectedBackground : normalBackground,
                new Pen(selected ? selectedBorder : normalBorder, 1), layout.Bounds, 7, 7);
            layout.Text.SetForegroundBrush(textBrush);
            drawingContext.DrawText(layout.Text, layout.TextOrigin);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var point = e.GetPosition(this);
        var layout = _layouts.FirstOrDefault(item => item.Bounds.Contains(point));
        if (layout is null || DataContext is not SnippetDetailViewModel viewModel) return;
        viewModel.ToggleBlock(layout.Block);
        InvalidateVisual();
        e.Handled = true;
    }

    private void BuildLayout(double width)
    {
        _layouts.Clear();
        _layoutWidth = width;
        if (Blocks is not { Count: > 0 }) return;

        var typeface = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;
        var maximumTokenWidth = Math.Max(80, width - TokenGap);

        foreach (var block in Blocks)
        {
            var text = new FormattedText(block.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, TokenFontSize, Brushes.White, pixelsPerDip)
            {
                MaxTextWidth = maximumTokenWidth - HorizontalPadding * 2
            };
            var tokenWidth = Math.Min(maximumTokenWidth, Math.Max(42, text.WidthIncludingTrailingWhitespace + HorizontalPadding * 2));
            text.MaxTextWidth = Math.Max(1, tokenWidth - HorizontalPadding * 2);
            var tokenHeight = Math.Max(40, text.Height + VerticalPadding * 2);

            if (x > 0 && x + tokenWidth > width)
            {
                x = 0;
                y += rowHeight + TokenGap;
                rowHeight = 0;
            }

            var bounds = new Rect(x, y, tokenWidth, tokenHeight);
            _layouts.Add(new TokenLayout(block, bounds, new Point(x + HorizontalPadding, y + VerticalPadding), text));
            x += tokenWidth + TokenGap;
            rowHeight = Math.Max(rowHeight, tokenHeight);
        }
    }

    private Brush FindBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private sealed record TokenLayout(ExplodedSnippetBlockViewModel Block, Rect Bounds, Point TextOrigin, FormattedText Text);
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
