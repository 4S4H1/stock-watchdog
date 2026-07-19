using System.Runtime.InteropServices;
using System.Windows;
using StockWatchdog.App.Services;

namespace StockWatchdog.App.Views;

public enum PortableConfigurationWindowMode
{
    Export,
    Import
}

public partial class PortableConfigurationWindow : Window
{
    private readonly PortableConfigurationWindowMode _mode;

    public PortableConfigurationWindow(
        PortableConfigurationWindowMode mode,
        string? initialText = null)
    {
        _mode = mode;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        ConfigurationTextBox.Text = initialText ?? string.Empty;
        if (mode == PortableConfigurationWindowMode.Export)
        {
            Title = "导出全部配置";
            TitleTextBlock.Text = "便携配置文本";
            DescriptionTextBlock.Text =
                "复制下面以 SWCFG1 开头的完整文本，在另一台设备的“导入配置文本”窗口中粘贴。"
                + "文本包含界面设置、自选列表、提醒规则和自定义主题，不包含行情缓存或历史提醒。";
            ConfigurationTextBox.IsReadOnly = true;
            PasteButton.Visibility = Visibility.Collapsed;
            ImportButton.Visibility = Visibility.Collapsed;
            Loaded += (_, _) =>
            {
                ConfigurationTextBox.Focus();
                ConfigurationTextBox.SelectAll();
            };
        }
        else
        {
            Title = "导入全部配置";
            TitleTextBlock.Text = "粘贴便携配置文本";
            DescriptionTextBlock.Text =
                "粘贴完整的 SWCFG1 配置文本。校验通过后会再次询问，确认后替换当前界面设置、"
                + "自选列表和提醒规则；行情缓存不会被修改。";
        }
    }

    public string ConfigurationText => ConfigurationTextBox.Text.Trim();

    private void OnPasteClick(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                StatusTextBlock.Text = "剪贴板中没有文本";
                return;
            }

            ConfigurationTextBox.Text = System.Windows.Clipboard.GetText().Trim();
            ConfigurationTextBox.CaretIndex = ConfigurationTextBox.Text.Length;
            StatusTextBlock.Text = "已从剪贴板粘贴";
        }
        catch (ExternalException exception)
        {
            StatusTextBlock.Text = $"无法读取剪贴板：{exception.Message}";
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(ConfigurationTextBox.Text))
        {
            StatusTextBlock.Text = "没有可复制的配置文本";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(ConfigurationTextBox.Text);
            StatusTextBlock.Text = "配置文本已复制到剪贴板";
        }
        catch (ExternalException exception)
        {
            StatusTextBlock.Text = $"无法写入剪贴板：{exception.Message}";
        }
    }

    private void OnImportClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_mode != PortableConfigurationWindowMode.Import)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ConfigurationTextBox.Text))
        {
            StatusTextBlock.Text = "请先粘贴配置文本";
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = false;
        Close();
    }
}
