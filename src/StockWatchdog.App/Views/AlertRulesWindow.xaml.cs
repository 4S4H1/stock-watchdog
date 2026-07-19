using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StockWatchdog.App.Services;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Application.Services;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.App.Views;

public partial class AlertRulesWindow : Window
{
    private readonly InstrumentId _instrument;
    private readonly IAppRepository _repository;
    private readonly MarketMonitorService _monitor;

    public AlertRulesWindow(
        InstrumentId instrument,
        string name,
        IAppRepository repository,
        MarketMonitorService monitor)
    {
        _instrument = instrument;
        _repository = repository;
        _monitor = monitor;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        TitleTextBlock.Text = $"{name} · 提醒规则";
        RuleTypeComboBox.ItemsSource = RuleTypes;
        RuleTypeComboBox.SelectedIndex = 0;
        PatternComboBox.ItemsSource = Patterns;
        PatternComboBox.SelectedIndex = 0;
        Loaded += async (_, _) => await ReloadAsync().ConfigureAwait(true);
    }

    private static IReadOnlyList<RuleTypeOption> RuleTypes { get; } =
    [
        new(AlertRuleType.PriceAbove, "价格向上触达"),
        new(AlertRuleType.PriceBelow, "价格向下触达"),
        new(AlertRuleType.ChangePercentAbove, "涨跌幅向上触达"),
        new(AlertRuleType.ChangePercentBelow, "涨跌幅向下触达"),
        new(AlertRuleType.Pattern, "技术形态")
    ];

    private static IReadOnlyList<PatternOption> Patterns { get; } =
    [
        new("vwap-mean-reversion-low", "VWAP 下方过度偏离"),
        new("vwap-mean-reversion-high", "VWAP 上方过度偏离"),
        new("volume-breakout-up", "放量向上突破"),
        new("volume-breakout-down", "放量向下突破"),
        new("breakout-pullback-up", "突破后缩量回踩"),
        new("breakout-pullback-down", "跌破后缩量反抽")
    ];

    private async Task ReloadAsync()
    {
        var rules = (await _repository.GetAlertRulesAsync().ConfigureAwait(true))
            .Where(x => x.Instrument == _instrument)
            .Select(x => new RuleRow(
                x,
                RuleTypes.FirstOrDefault(y => y.Type == x.Type)?.Label ?? x.Type.ToString(),
                x.Type == AlertRuleType.Pattern
                    ? Patterns.FirstOrDefault(y => y.Id == x.PatternId)?.Label ?? x.PatternId ?? "--"
                    : x.Threshold?.ToString(CultureInfo.InvariantCulture) ?? "--"))
            .ToArray();
        RulesGrid.ItemsSource = rules;
    }

    private void OnRuleTypeSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        var pattern = RuleTypeComboBox.SelectedItem is RuleTypeOption
        {
            Type: AlertRuleType.Pattern
        };
        ThresholdTextBox.IsEnabled = !pattern;
        PatternComboBox.IsEnabled = pattern;
    }

    private async void OnAddRuleClick(object sender, RoutedEventArgs eventArgs)
    {
        if (RuleTypeComboBox.SelectedItem is not RuleTypeOption type
            || !int.TryParse(CooldownTextBox.Text, out var cooldown)
            || !int.TryParse(MaxTriggersTextBox.Text, out var maximum)
            || cooldown is < 1 or > 240
            || maximum is < 1 or > 20)
        {
            StatusTextBlock.Text = "请检查冷却时间和每日次数";
            return;
        }

        decimal? threshold = null;
        string? patternId = null;
        if (type.Type == AlertRuleType.Pattern)
        {
            patternId = (PatternComboBox.SelectedItem as PatternOption)?.Id;
        }
        else if (!decimal.TryParse(
                     ThresholdTextBox.Text,
                     NumberStyles.Float,
                     CultureInfo.CurrentCulture,
                     out var parsed)
                 && !decimal.TryParse(
                     ThresholdTextBox.Text,
                     NumberStyles.Float,
                     CultureInfo.InvariantCulture,
                     out parsed))
        {
            StatusTextBlock.Text = "请填写有效阈值";
            return;
        }
        else
        {
            threshold = parsed;
        }

        var name = string.IsNullOrWhiteSpace(RuleNameTextBox.Text)
            ? type.Label
            : RuleNameTextBox.Text.Trim();
        var rule = new AlertRule(
            Guid.NewGuid(),
            _instrument,
            type.Type,
            name,
            threshold,
            patternId,
            RuleEnabledCheckBox.IsChecked == true,
            TimeSpan.FromMinutes(cooldown),
            TimeSpan.FromMinutes(10),
            maximum,
            AlertPriority.Normal,
            "1.0",
            DateTimeOffset.Now);
        try
        {
            await _repository.UpsertAlertRuleAsync(rule).ConfigureAwait(true);
            await _monitor.ReloadConfigurationAsync().ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            StatusTextBlock.Text = "规则已添加";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
    }

    private async void OnDeleteRuleClick(object sender, RoutedEventArgs eventArgs)
    {
        if (RulesGrid.SelectedItem is not RuleRow row)
        {
            StatusTextBlock.Text = "请先选择规则";
            return;
        }

        try
        {
            await _repository.DeleteAlertRuleAsync(row.Rule.Id).ConfigureAwait(true);
            await _monitor.ReloadConfigurationAsync().ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            StatusTextBlock.Text = "规则已删除";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs) => Close();

    private sealed record RuleTypeOption(AlertRuleType Type, string Label);

    private sealed record PatternOption(string Id, string Label);

    private sealed record RuleRow(AlertRule Rule, string TypeText, string TargetText);
}
