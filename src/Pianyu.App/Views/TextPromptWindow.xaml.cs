using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Pianyu.App.Views;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string label, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptTitle.Text = title;
        PromptLabel.Text = label;
        ValueBox.Text = initial;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }
    public string Value => ValueBox.Text.Trim();
    private void Confirm_OnClick(object sender, RoutedEventArgs e) { if (!string.IsNullOrWhiteSpace(Value)) DialogResult = true; }
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Window_OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Confirm_OnClick(sender, e); else if (e.Key == Key.Escape) DialogResult = false; }
}
