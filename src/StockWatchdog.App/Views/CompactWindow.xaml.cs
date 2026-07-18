using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StockWatchdog.App.ViewModels;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.App.Views;

public partial class CompactWindow : Window
{
    private bool _allowClose;
    private bool _isEditingName;
    private bool _isMinimalMode;

    public CompactWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? HideRequested;

    public void ApplySettings(AppSettings settings, ThemeDefinition theme)
    {
        Topmost = settings.AlwaysOnTop;
        Opacity = theme.Opacity;
        _isMinimalMode = settings.CompactMode == CompactDisplayMode.Minimal;

        TitleRow.Height = _isMinimalMode ? new GridLength(0) : new GridLength(34);
        CommandRow.Height = _isMinimalMode ? new GridLength(0) : new GridLength(42);
        StatusRow.Height = _isMinimalMode ? new GridLength(0) : new GridLength(30);
        TitleBar.Visibility = _isMinimalMode ? Visibility.Collapsed : Visibility.Visible;
        CommandBar.Visibility = _isMinimalMode ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.Visibility = _isMinimalMode ? Visibility.Collapsed : Visibility.Visible;
        WatchGrid.Margin = _isMinimalMode ? new Thickness(0) : new Thickness(8, 0, 8, 0);
        MinWidth = _isMinimalMode ? 300 : 660;
        MinHeight = _isMinimalMode ? 100 : 210;

        NameColumn.Visibility = settings.ShowNameColumn
            ? Visibility.Visible
            : Visibility.Collapsed;
        CodeColumn.Visibility = settings.ShowCodeColumn ? Visibility.Visible : Visibility.Collapsed;
        PriceColumn.Visibility = settings.ShowPriceColumn
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChangeColumn.Visibility = settings.ShowChangeColumn
            ? Visibility.Visible
            : Visibility.Collapsed;
        SignalColumn.Visibility = settings.ShowSignalColumn ? Visibility.Visible : Visibility.Collapsed;
        SparklineColumn.Visibility = settings.ShowSparklineColumn
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateTimeColumn.Visibility = settings.ShowUpdateTimeColumn
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!settings.ShowNameColumn
            && !settings.ShowCodeColumn
            && !settings.ShowPriceColumn
            && !settings.ShowChangeColumn
            && !settings.ShowSignalColumn
            && !settings.ShowSparklineColumn
            && !settings.ShowUpdateTimeColumn)
        {
            NameColumn.Visibility = Visibility.Visible;
        }
    }

    public void AllowClose() => _allowClose = true;

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnHideClick(object sender, RoutedEventArgs eventArgs) =>
        HideRequested?.Invoke(this, EventArgs.Empty);

    private void OnInstrumentTextKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter
            && DataContext is ViewModels.MainViewModel viewModel
            && viewModel.AddInstrumentCommand.CanExecute(null))
        {
            viewModel.AddInstrumentCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape
            && DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.CloseDetailCommand.Execute(null);
        }
    }

    private void OnWatchGridBeginningEdit(
        object sender,
        DataGridBeginningEditEventArgs eventArgs)
    {
        if (eventArgs.Column != NameColumn)
        {
            eventArgs.Cancel = true;
            return;
        }

        _isEditingName = true;
    }

    private void OnWatchGridPreparingCellForEdit(
        object sender,
        DataGridPreparingCellForEditEventArgs eventArgs)
    {
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                var editor = FindDescendant<System.Windows.Controls.TextBox>(
                    eventArgs.EditingElement);
                editor?.Focus();
                editor?.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private async void OnWatchGridCellEditEnding(
        object sender,
        DataGridCellEditEndingEventArgs eventArgs)
    {
        _isEditingName = false;
        if (eventArgs.EditAction != DataGridEditAction.Commit
            || eventArgs.Row.Item is not WatchRowViewModel row
            || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var editor = FindDescendant<System.Windows.Controls.TextBox>(
            eventArgs.EditingElement);
        await viewModel.RenameWatchItemAsync(row, editor?.Text).ConfigureAwait(true);
    }

    private void OnWatchGridPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.F2 && !_isEditingName)
        {
            BeginRename();
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key == Key.Enter && !_isEditingName)
        {
            OpenSelectedDetail();
            eventArgs.Handled = true;
        }
    }

    private void OnWatchGridPreviewMouseDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        var row = FindAncestor<DataGridRow>(eventArgs.OriginalSource as DependencyObject);
        if (row?.Item is not WatchRowViewModel selected)
        {
            return;
        }

        if (_isEditingName)
        {
            WatchGrid.CancelEdit(DataGridEditingUnit.Cell);
            _isEditingName = false;
        }

        WatchGrid.SelectedItem = selected;
        OpenSelectedDetail();
        eventArgs.Handled = true;
    }

    private void OnWatchGridPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!_isMinimalMode
            || eventArgs.LeftButton != MouseButtonState.Pressed
            || !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            return;
        }

        DragMove();
        eventArgs.Handled = true;
    }

    private void OnWatchGridPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        var row = FindAncestor<DataGridRow>(eventArgs.OriginalSource as DependencyObject);
        if (row?.Item is WatchRowViewModel selected)
        {
            WatchGrid.SelectedItem = selected;
        }
    }

    private void OnRenameClick(object sender, RoutedEventArgs eventArgs) => BeginRename();

    private void BeginRename()
    {
        if (WatchGrid.SelectedItem is not WatchRowViewModel selected)
        {
            return;
        }

        WatchGrid.Focus();
        WatchGrid.CurrentCell = new DataGridCellInfo(selected, NameColumn);
        WatchGrid.ScrollIntoView(selected, NameColumn);
        _ = WatchGrid.BeginEdit();
    }

    private void OpenSelectedDetail()
    {
        if (DataContext is MainViewModel viewModel
            && viewModel.OpenDetailCommand.CanExecute(null))
        {
            viewModel.OpenDetailCommand.Execute(null);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject? current)
        where T : DependencyObject
    {
        if (current is null)
        {
            return null;
        }

        if (current is T match)
        {
            return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
        {
            var descendant = FindDescendant<T>(VisualTreeHelper.GetChild(current, index));
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
    }
}
