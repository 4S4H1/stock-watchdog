using System.Collections.ObjectModel;
using System.Windows.Input;
using StockWatchdog.App.Infrastructure;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Application.Services;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.App.ViewModels;

public sealed record TimeframeOption(Timeframe Value, string Label);

public sealed class MainViewModel : ObservableObject
{
    private readonly IAppRepository _repository;
    private readonly MarketMonitorService _monitor;
    private WatchRowViewModel? _selectedRow;
    private string _newInstrumentText = string.Empty;
    private string _statusText = "正在启动…";
    private TimeframeOption _selectedTimeframe;
    private AnalysisSnapshot? _selectedAnalysis;
    private IReadOnlyList<decimal> _alertThresholds = [];

    public MainViewModel(IAppRepository repository, MarketMonitorService monitor)
    {
        _repository = repository;
        _monitor = monitor;
        Timeframes =
        [
            new(Timeframe.Minute1, "1 分钟"),
            new(Timeframe.Minute5, "5 分钟"),
            new(Timeframe.Minute15, "15 分钟"),
            new(Timeframe.Minute60, "60 分钟"),
            new(Timeframe.Day, "日线")
        ];
        _selectedTimeframe = Timeframes[0];

        AddInstrumentCommand = new AsyncRelayCommand(AddInstrumentAsync, SetError);
        RemoveSelectedCommand = new AsyncRelayCommand(RemoveSelectedAsync, SetError, () => SelectedRow is not null);
        MoveUpCommand = new AsyncRelayCommand(() => MoveSelectedAsync(-1), SetError, () => SelectedRow is not null);
        MoveDownCommand = new AsyncRelayCommand(() => MoveSelectedAsync(1), SetError, () => SelectedRow is not null);
        RefreshCommand = new AsyncRelayCommand(
            () => _monitor.RefreshNowAsync(),
            SetError);
        OpenSettingsCommand = new RelayCommand(
            () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        OpenAlertsCommand = new RelayCommand(
            () => AlertRulesRequested?.Invoke(this, EventArgs.Empty),
            () => SelectedRow is not null);
        OpenDetailCommand = new RelayCommand(
            () => DetailRequested?.Invoke(this, EventArgs.Empty),
            () => SelectedRow is not null);
        CloseDetailCommand = new RelayCommand(
            () => DetailCloseRequested?.Invoke(this, EventArgs.Empty));

        _monitor.QuotesUpdated += OnQuotesUpdated;
        _monitor.AnalysisUpdated += OnAnalysisUpdated;
        _monitor.TSignalUpdated += OnTSignalUpdated;
        _monitor.StatusChanged += OnStatusChanged;
    }

    public ObservableCollection<WatchRowViewModel> Rows { get; } = [];

    public IReadOnlyList<TimeframeOption> Timeframes { get; }

    public string NewInstrumentText
    {
        get => _newInstrumentText;
        set => SetProperty(ref _newInstrumentText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public WatchRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value))
            {
                return;
            }

            NotifyCommandStates();
            RefreshSelectedAnalysis();
            _ = LoadAlertThresholdsAsync();
        }
    }

    public TimeframeOption SelectedTimeframe
    {
        get => _selectedTimeframe;
        set
        {
            if (SetProperty(ref _selectedTimeframe, value))
            {
                RefreshSelectedAnalysis();
            }
        }
    }

    public AnalysisSnapshot? SelectedAnalysis
    {
        get => _selectedAnalysis;
        private set
        {
            if (SetProperty(ref _selectedAnalysis, value))
            {
                OnPropertyChanged(nameof(Findings));
                OnPropertyChanged(nameof(AnalysisStatus));
                OnPropertyChanged(nameof(RegimeText));
                OnPropertyChanged(nameof(IndicatorSummary));
                OnPropertyChanged(nameof(IsAnalysisHealthy));
                AnalysisChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public IReadOnlyList<PatternFinding> Findings => SelectedAnalysis?.Findings ?? [];

    public string AnalysisStatus => SelectedAnalysis?.StatusText ?? "等待分钟数据";

    public string RegimeText => SelectedAnalysis?.Regime switch
    {
        MarketRegime.TrendingUp => "趋势：偏强",
        MarketRegime.TrendingDown => "趋势：偏弱",
        MarketRegime.Ranging => "趋势：震荡",
        _ => "趋势：判断中"
    };

    public string IndicatorSummary
    {
        get
        {
            var latest = SelectedAnalysis?.Indicators.LastOrDefault();
            return latest is null
                ? "VWAP / RSI / ATR 预热中"
                : $"VWAP {Format(latest.Vwap)} · RSI {Format(latest.Rsi14)} · ATR {Format(latest.Atr14)} · MACD {Format(latest.Macd)}";
        }
    }

    public bool IsAnalysisHealthy =>
        SelectedAnalysis?.DataQuality == MarketDataQuality.Healthy;

    public IReadOnlyList<decimal> AlertThresholds
    {
        get => _alertThresholds;
        private set
        {
            if (SetProperty(ref _alertThresholds, value))
            {
                AnalysisChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public ICommand AddInstrumentCommand { get; }

    public ICommand RemoveSelectedCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenAlertsCommand { get; }

    public ICommand OpenDetailCommand { get; }

    public ICommand CloseDetailCommand { get; }

    public event EventHandler? DetailRequested;

    public event EventHandler? DetailCloseRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? AlertRulesRequested;

    public event EventHandler? AnalysisChanged;

    public async Task InitializeAsync()
    {
        Rows.Clear();
        foreach (var item in _monitor.WatchItems.OrderBy(x => x.SortOrder))
        {
            var row = new WatchRowViewModel(item);
            if (_monitor.LatestTSignals.TryGetValue(item.Instrument, out var signal))
            {
                row.UpdateTSignal(signal);
            }

            Rows.Add(row);
        }

        if (Rows.Count > 0)
        {
            SelectedRow = Rows[0];
        }

        await Task.CompletedTask;
    }

    public async Task<bool> RenameWatchItemAsync(
        WatchRowViewModel row,
        string? customName)
    {
        if (!Rows.Contains(row))
        {
            return false;
        }

        var normalized = string.IsNullOrWhiteSpace(customName)
            ? null
            : customName.Trim();
        if (normalized?.Length > 24)
        {
            StatusText = "自定义名称最多 24 个字符";
            return false;
        }

        try
        {
            var updated = row.Item with { CustomName = normalized };
            await _repository.UpsertWatchItemAsync(updated).ConfigureAwait(true);
            row.ReplaceItem(updated);
            await _monitor.ReloadConfigurationAsync().ConfigureAwait(true);
            StatusText = normalized is null
                ? $"{row.Instrument.Code} 已恢复行情名称"
                : $"{row.Instrument.Code} 已重命名为 {normalized}";
            return true;
        }
        catch (Exception exception)
        {
            SetError(exception);
            return false;
        }
    }

    private async Task AddInstrumentAsync()
    {
        if (!InstrumentId.TryParse(NewInstrumentText, out var instrument))
        {
            StatusText = "请输入 6 位代码，例如 600519、000001、SH600000";
            return;
        }

        if (Rows.Any(x => x.Instrument == instrument))
        {
            SelectedRow = Rows.First(x => x.Instrument == instrument);
            NewInstrumentText = string.Empty;
            return;
        }

        if (Rows.Count >= _monitor.Settings.MaximumWatchItems)
        {
            StatusText = $"最多监控 {_monitor.Settings.MaximumWatchItems} 个标的";
            return;
        }

        var item = new WatchItem(instrument, instrument.Code, Rows.Count);
        await _repository.UpsertWatchItemAsync(item).ConfigureAwait(true);
        var row = new WatchRowViewModel(item);
        Rows.Add(row);
        NewInstrumentText = string.Empty;
        await _monitor.ReloadConfigurationAsync().ConfigureAwait(true);
        SelectedRow = row;
        await _monitor.RefreshNowAsync().ConfigureAwait(true);
    }

    private async Task RemoveSelectedAsync()
    {
        if (SelectedRow is not { } selected)
        {
            return;
        }

        var index = Rows.IndexOf(selected);
        await _repository.DeleteWatchItemAsync(selected.Instrument).ConfigureAwait(true);
        Rows.Remove(selected);
        SelectedRow = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];
        await PersistOrderAsync().ConfigureAwait(true);
        await _monitor.ReloadConfigurationAsync().ConfigureAwait(true);
    }

    private async Task MoveSelectedAsync(int direction)
    {
        if (SelectedRow is not { } selected)
        {
            return;
        }

        var oldIndex = Rows.IndexOf(selected);
        var newIndex = Math.Clamp(oldIndex + direction, 0, Rows.Count - 1);
        if (oldIndex == newIndex)
        {
            return;
        }

        Rows.Move(oldIndex, newIndex);
        await PersistOrderAsync().ConfigureAwait(true);
        await _monitor.ReloadConfigurationAsync().ConfigureAwait(true);
    }

    private async Task PersistOrderAsync()
    {
        for (var index = 0; index < Rows.Count; index++)
        {
            var item = Rows[index].Item with { SortOrder = index };
            Rows[index].ReplaceItem(item);
            await _repository.UpsertWatchItemAsync(item).ConfigureAwait(true);
        }
    }

    private void OnQuotesUpdated(object? sender, QuoteBatchEventArgs eventArgs)
    {
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var quote in eventArgs.Quotes)
            {
                Rows.FirstOrDefault(x => x.Instrument == quote.Instrument)?.UpdateQuote(quote);
            }
        });
    }

    private void OnAnalysisUpdated(object? sender, AnalysisEventArgs eventArgs)
    {
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var snapshot = eventArgs.Snapshot;
            var row = Rows.FirstOrDefault(x => x.Instrument == snapshot.Instrument);
            if (snapshot.Timeframe == Timeframe.Minute1)
            {
                row?.UpdateSparkline(snapshot);
            }

            if (snapshot.Timeframe == Timeframe.Minute5)
            {
                row?.UpdateAnalysis(snapshot);
            }

            if (SelectedRow?.Instrument == snapshot.Instrument
                && SelectedTimeframe.Value == snapshot.Timeframe)
            {
                SelectedAnalysis = snapshot;
            }
        });
    }

    private void OnStatusChanged(object? sender, MonitorStatusEventArgs eventArgs)
    {
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(
            () => StatusText = eventArgs.Message);
    }

    private void OnTSignalUpdated(object? sender, TSignalEventArgs eventArgs)
    {
        _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Rows.FirstOrDefault(x => x.Instrument == eventArgs.Snapshot.Instrument)
                ?.UpdateTSignal(eventArgs.Snapshot);
        });
    }

    private void RefreshSelectedAnalysis()
    {
        SelectedAnalysis = SelectedRow is null
            ? null
            : _monitor.GetAnalysis(SelectedRow.Instrument, SelectedTimeframe.Value);
    }

    private async Task LoadAlertThresholdsAsync()
    {
        try
        {
            if (SelectedRow is not { } selected)
            {
                AlertThresholds = [];
                return;
            }

            AlertThresholds = (await _repository.GetAlertRulesAsync().ConfigureAwait(true))
                .Where(x => x.Instrument == selected.Instrument && x.Enabled && x.Threshold is not null)
                .Select(x => x.Threshold!.Value)
                .Distinct()
                .Order()
                .ToArray();
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
    }

    private void NotifyCommandStates()
    {
        (RemoveSelectedCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (MoveUpCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (MoveDownCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (OpenAlertsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (OpenDetailCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void SetError(Exception exception) => StatusText = $"操作失败：{exception.Message}";

    private static string Format(decimal? value) =>
        value?.ToString("0.###") ?? "--";
}
