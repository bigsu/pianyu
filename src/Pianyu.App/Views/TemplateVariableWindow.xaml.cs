using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Pianyu.App.Infrastructure;
using Pianyu.App.ViewModels;
using Pianyu.Core;

namespace Pianyu.App.Views;

public sealed class TemplateVariablesViewModel : ObservableObject
{
    public required string TemplateText { get; init; }
    public ObservableCollection<TemplateVariableItemViewModel> Items { get; } = [];
}

public partial class TemplateVariableWindow : Window
{
    private readonly AppServices _services;
    private readonly TemplateVariablesViewModel _viewModel;
    public string RenderedText { get; private set; } = string.Empty;

    public TemplateVariableWindow(AppServices services, string templateText, IReadOnlyList<TemplateVariable> variables)
    {
        InitializeComponent();
        _services = services;
        _viewModel = new TemplateVariablesViewModel { TemplateText = templateText };
        foreach (var variable in variables) _viewModel.Items.Add(new TemplateVariableItemViewModel { Name = variable.Name, DefaultValue = variable.DefaultValue, Value = variable.DefaultValue });
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            foreach (var item in _viewModel.Items)
            {
                var recent = await services.Repository.GetRecentTemplateValuesAsync(item.Name);
                foreach (var value in recent) item.RecentValues.Add(value);
            }
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        };
    }

    private async void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        var values = _viewModel.Items.ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
        RenderedText = TemplateEngine.Render(_viewModel.TemplateText, values);
        foreach (var item in _viewModel.Items) await _services.Repository.SaveTemplateValueAsync(item.Name, item.Value);
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void RecentValue_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string selected })
        {
            var parent = FindVariableItem((DependencyObject)sender);
            if (parent is not null) parent.Value = selected;
        }
    }

    private static TemplateVariableItemViewModel? FindVariableItem(DependencyObject element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: TemplateVariableItemViewModel item }) return item;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }
    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { Confirm_OnClick(sender, e); e.Handled = true; }
    }
}
