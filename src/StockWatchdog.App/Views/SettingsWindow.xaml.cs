using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StockWatchdog.App.Services;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Application.Services;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.App.Views;

public partial class SettingsWindow : Window
{
    private readonly IAppRepository _repository;
    private readonly MarketMonitorService _monitor;
    private readonly ThemeManager _themeManager;
    private readonly Func<HotkeySettings, (bool Success, string? Error)> _registerHotkey;
    private AppSettings _settings = new();

    public SettingsWindow(
        IAppRepository repository,
        MarketMonitorService monitor,
        ThemeManager themeManager,
        Func<HotkeySettings, (bool Success, string? Error)> registerHotkey)
    {
        _repository = repository;
        _monitor = monitor;
        _themeManager = themeManager;
        _registerHotkey = registerHotkey;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event EventHandler<AppSettings>? SettingsSaved;

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            _settings = await _repository.GetSettingsAsync().ConfigureAwait(true);
            RefreshSecondsTextBox.Text = _settings.RefreshSeconds.ToString();
            HotkeyTextBox.Text = _settings.EffectiveBossHotkey.DisplayText;
            AlwaysOnTopCheckBox.IsChecked = _settings.AlwaysOnTop;
            SoundEnabledCheckBox.IsChecked = _settings.SoundEnabled;
            StartHiddenCheckBox.IsChecked = _settings.StartHidden;
            StartWithWindowsCheckBox.IsChecked = _settings.StartWithWindows;
            CompactModeComboBox.SelectedIndex =
                _settings.CompactMode == CompactDisplayMode.Minimal ? 1 : 0;
            ShowNameCheckBox.IsChecked = _settings.ShowNameColumn;
            ShowCodeCheckBox.IsChecked = _settings.ShowCodeColumn;
            ShowPriceCheckBox.IsChecked = _settings.ShowPriceColumn;
            ShowChangeCheckBox.IsChecked = _settings.ShowChangeColumn;
            ShowSignalCheckBox.IsChecked = _settings.ShowSignalColumn;
            ShowSparklineCheckBox.IsChecked = _settings.ShowSparklineColumn;
            ShowTimeCheckBox.IsChecked = _settings.ShowUpdateTimeColumn;
            ReloadThemes(_settings.ThemeId);
        }
        catch (Exception exception)
        {
            ErrorTextBlock.Text = exception.Message;
        }
    }

    private void ReloadThemes(string? selectedId)
    {
        ThemeComboBox.ItemsSource = null;
        ThemeComboBox.ItemsSource = _themeManager.Themes;
        ThemeComboBox.SelectedItem = _themeManager.Themes.FirstOrDefault(
            x => string.Equals(x.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            ?? _themeManager.Themes.FirstOrDefault();
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (ThemeComboBox.SelectedItem is ThemeDefinition theme)
        {
            _themeManager.Apply(theme.Id);
        }
    }

    private void OnImportThemeClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入主题",
            Filter = "StockWatchdog 主题 (*.json)|*.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var theme = _themeManager.Import(dialog.FileName);
            ReloadThemes(theme.Id);
            _themeManager.Apply(theme.Id);
            ErrorTextBlock.Text = "主题已导入";
        }
        catch (Exception exception)
        {
            ErrorTextBlock.Text = $"导入失败：{exception.Message}";
        }
    }

    private void OnExportThemeClick(object sender, RoutedEventArgs eventArgs)
    {
        if (ThemeComboBox.SelectedItem is not ThemeDefinition theme)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出主题",
            Filter = "StockWatchdog 主题 (*.json)|*.json",
            FileName = $"{theme.Id}.json"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _themeManager.Export(theme, dialog.FileName);
            ErrorTextBlock.Text = "主题已导出";
        }
        catch (Exception exception)
        {
            ErrorTextBlock.Text = $"导出失败：{exception.Message}";
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs eventArgs)
    {
        ErrorTextBlock.Text = string.Empty;
        if (!int.TryParse(RefreshSecondsTextBox.Text, out var refreshSeconds)
            || refreshSeconds is < 3 or > 60)
        {
            ErrorTextBlock.Text = "刷新秒数必须在 3–60 之间";
            return;
        }

        if (!HotkeyParser.TryParse(HotkeyTextBox.Text, out var hotkey, out var parseError))
        {
            ErrorTextBlock.Text = parseError;
            return;
        }

        if (ShowNameCheckBox.IsChecked != true
            && ShowCodeCheckBox.IsChecked != true
            && ShowPriceCheckBox.IsChecked != true
            && ShowChangeCheckBox.IsChecked != true
            && ShowSignalCheckBox.IsChecked != true
            && ShowSparklineCheckBox.IsChecked != true
            && ShowTimeCheckBox.IsChecked != true)
        {
            ErrorTextBlock.Text = "小浮窗至少需要显示一列";
            return;
        }

        var registration = _registerHotkey(hotkey);
        if (!registration.Success)
        {
            ErrorTextBlock.Text = $"快捷键冲突：{registration.Error}";
            return;
        }

        var theme = ThemeComboBox.SelectedItem as ThemeDefinition ?? _themeManager.Current;
        var updated = _settings with
        {
            RefreshSeconds = refreshSeconds,
            ThemeId = theme.Id,
            AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true,
            SoundEnabled = SoundEnabledCheckBox.IsChecked == true,
            StartHidden = StartHiddenCheckBox.IsChecked == true,
            StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
            CompactMode = CompactModeComboBox.SelectedIndex == 1
                ? CompactDisplayMode.Minimal
                : CompactDisplayMode.Rich,
            ShowNameColumn = ShowNameCheckBox.IsChecked == true,
            ShowCodeColumn = ShowCodeCheckBox.IsChecked == true,
            ShowPriceColumn = ShowPriceCheckBox.IsChecked == true,
            ShowChangeColumn = ShowChangeCheckBox.IsChecked == true,
            ShowSignalColumn = ShowSignalCheckBox.IsChecked == true,
            ShowSparklineColumn = ShowSparklineCheckBox.IsChecked == true,
            ShowUpdateTimeColumn = ShowTimeCheckBox.IsChecked == true,
            BossHotkey = hotkey
        };

        try
        {
            await _repository.SaveSettingsAsync(updated).ConfigureAwait(true);
            await _monitor.ReloadConfigurationAsync().ConfigureAwait(true);
            _themeManager.Apply(updated.ThemeId);
            SettingsSaved?.Invoke(this, updated);
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            _ = _registerHotkey(_settings.EffectiveBossHotkey);
            try
            {
                await _repository.SaveSettingsAsync(_settings).ConfigureAwait(true);
                await _monitor.ReloadConfigurationAsync().ConfigureAwait(true);
            }
            catch (Exception)
            {
            }

            ErrorTextBlock.Text = $"保存失败：{exception.Message}";
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs eventArgs)
    {
        _themeManager.Apply(_settings.ThemeId);
        DialogResult = false;
        Close();
    }
}
