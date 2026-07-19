using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StockWatchdog.App.Services;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Application.Configuration;
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
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        Loaded += OnLoaded;
    }

    public event EventHandler<AppSettings>? SettingsSaved;

    public event EventHandler<AppSettings>? ConfigurationImported;

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
            TScoreEnabledCheckBox.IsChecked = _settings.TScoreEnabled;
            TScoreThresholdTextBox.Text = _settings.TScoreAlertThreshold.ToString();
            TScoreCooldownTextBox.Text = _settings.TScoreCooldownMinutes.ToString();
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

    private async void OnExportConfigurationClick(object sender, RoutedEventArgs eventArgs)
    {
        ErrorTextBlock.Text = string.Empty;
        try
        {
            var bundle = new PortableConfigurationBundle(
                PortableConfigurationCodec.CurrentFormatVersion,
                "StockWatchdog",
                DateTimeOffset.Now,
                await _repository.GetSettingsAsync().ConfigureAwait(true),
                await _repository.GetWatchItemsAsync().ConfigureAwait(true),
                await _repository.GetAlertRulesAsync().ConfigureAwait(true),
                _themeManager.GetPortableThemes());
            var text = PortableConfigurationCodec.Encode(bundle);
            var window = new PortableConfigurationWindow(
                PortableConfigurationWindowMode.Export,
                text)
            {
                Owner = this
            };
            _ = window.ShowDialog();
            ErrorTextBlock.Text = $"已生成配置文本（{text.Length:N0} 个字符）";
        }
        catch (Exception exception)
        {
            ErrorTextBlock.Text = $"导出失败：{exception.Message}";
        }
    }

    private async void OnImportConfigurationClick(object sender, RoutedEventArgs eventArgs)
    {
        ErrorTextBlock.Text = string.Empty;
        var window = new PortableConfigurationWindow(PortableConfigurationWindowMode.Import)
        {
            Owner = this
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        PortableConfigurationBundle bundle;
        try
        {
            bundle = PortableConfigurationCodec.Decode(window.ConfigurationText);
        }
        catch (Exception exception)
        {
            ErrorTextBlock.Text = $"配置校验失败：{exception.Message}";
            return;
        }

        var confirmation = System.Windows.MessageBox.Show(
            $"配置导出时间：{bundle.ExportedAt:yyyy-MM-dd HH:mm}\n"
            + $"自选标的：{bundle.WatchItems.Count} 个\n"
            + $"提醒规则：{bundle.AlertRules.Count} 条\n"
            + $"自定义主题：{bundle.CustomThemes.Count} 个\n\n"
            + "确认后将替换当前界面设置、自选列表和提醒规则。"
            + "行情缓存、图表标记和历史提醒不会被修改。",
            "确认导入全部配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var registration = _registerHotkey(bundle.Settings.EffectiveBossHotkey);
        if (!registration.Success)
        {
            ErrorTextBlock.Text = $"导入配置中的快捷键不可用：{registration.Error}";
            return;
        }

        try
        {
            _themeManager.InstallPortableThemes(bundle.CustomThemes);
            if (!_themeManager.Themes.Any(theme => string.Equals(
                    theme.Id,
                    bundle.Settings.ThemeId,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"配置引用了不存在的主题：{bundle.Settings.ThemeId}");
            }

            await _repository.ReplacePortableConfigurationAsync(
                    bundle.Settings,
                    bundle.WatchItems,
                    bundle.AlertRules)
                .ConfigureAwait(true);
            await _monitor.ReloadConfigurationAsync().ConfigureAwait(true);
            _settings = bundle.Settings.Normalize();
            _themeManager.Apply(_settings.ThemeId);
            ConfigurationImported?.Invoke(this, _settings);
            System.Windows.MessageBox.Show(
                "配置已导入并应用。行情缓存、图表标记和历史提醒保持不变。",
                "配置导入完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            _ = _registerHotkey(_settings.EffectiveBossHotkey);
            _themeManager.Apply(_settings.ThemeId);
            ErrorTextBlock.Text = $"导入失败，原配置保持不变：{exception.Message}";
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

        if (!int.TryParse(TScoreThresholdTextBox.Text, out var tScoreThreshold)
            || tScoreThreshold is < 60 or > 95
            || !int.TryParse(TScoreCooldownTextBox.Text, out var tScoreCooldown)
            || tScoreCooldown is < 1 or > 240)
        {
            ErrorTextBlock.Text = "做 T 评分阈值需为 60–95，冷却时间需为 1–240 分钟";
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
            TScoreEnabled = TScoreEnabledCheckBox.IsChecked == true,
            TScoreAlertThreshold = tScoreThreshold,
            TScoreCooldownMinutes = tScoreCooldown,
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
