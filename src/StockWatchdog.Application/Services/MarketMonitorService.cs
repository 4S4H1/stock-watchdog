using System.Collections.Concurrent;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Application.Alerts;
using StockWatchdog.Application.Analysis;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.Application.Services;

public sealed class QuoteBatchEventArgs(IReadOnlyList<QuoteSnapshot> quotes) : EventArgs
{
    public IReadOnlyList<QuoteSnapshot> Quotes { get; } = quotes;
}

public sealed class AnalysisEventArgs(AnalysisSnapshot snapshot) : EventArgs
{
    public AnalysisSnapshot Snapshot { get; } = snapshot;
}

public sealed class AlertEventArgs(AlertEvent alert) : EventArgs
{
    public AlertEvent Alert { get; } = alert;
}

public sealed class MonitorStatusEventArgs(string message, bool isError = false) : EventArgs
{
    public string Message { get; } = message;

    public bool IsError { get; } = isError;
}

public sealed class MarketMonitorService : IAsyncDisposable
{
    private readonly IMarketDataProvider _provider;
    private readonly IAppRepository _repository;
    private readonly ITechnicalAnalysisEngine _analysisEngine;
    private readonly IClock _clock;
    private readonly AlertEvaluator _alertEvaluator;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly ConcurrentDictionary<InstrumentId, QuoteSnapshot> _quotes = [];
    private readonly ConcurrentDictionary<(InstrumentId, Timeframe), AnalysisSnapshot> _analysis = [];

    private CancellationTokenSource? _cancellation;
    private Task? _monitorTask;
    private AppSettings _settings = new();
    private IReadOnlyList<WatchItem> _watchItems = [];
    private IReadOnlyList<AlertRule> _alertRules = [];
    private DateTimeOffset _lastAnalysisMinute;

    public MarketMonitorService(
        IMarketDataProvider provider,
        IAppRepository repository,
        ITechnicalAnalysisEngine analysisEngine,
        IClock clock,
        AlertEvaluator alertEvaluator)
    {
        _provider = provider;
        _repository = repository;
        _analysisEngine = analysisEngine;
        _clock = clock;
        _alertEvaluator = alertEvaluator;
    }

    public event EventHandler<QuoteBatchEventArgs>? QuotesUpdated;

    public event EventHandler<AnalysisEventArgs>? AnalysisUpdated;

    public event EventHandler<AlertEventArgs>? AlertRaised;

    public event EventHandler<MonitorStatusEventArgs>? StatusChanged;

    public AppSettings Settings => _settings;

    public IReadOnlyList<WatchItem> WatchItems => _watchItems;

    public IReadOnlyDictionary<InstrumentId, QuoteSnapshot> LatestQuotes => _quotes;

    public IReadOnlyDictionary<(InstrumentId, Timeframe), AnalysisSnapshot> LatestAnalysis => _analysis;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_monitorTask is not null)
        {
            return;
        }

        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await ReloadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = Task.Run(() => RunAsync(_cancellation.Token), CancellationToken.None);
    }

    public async Task ReloadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        _settings = (await _repository.GetSettingsAsync(cancellationToken).ConfigureAwait(false))
            .Normalize();
        _watchItems = (await _repository.GetWatchItemsAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(x => x.SortOrder)
            .Take(_settings.MaximumWatchItems)
            .ToArray();
        _alertRules = await _repository.GetAlertRulesAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.Now;
        var localDayStart = new DateTimeOffset(now.Date, now.Offset);
        var recentAlerts = await _repository
            .GetAlertEventsAsync(localDayStart, cancellationToken)
            .ConfigureAwait(false);
        _alertEvaluator.RestoreState(_alertRules, recentAlerts);
        StatusChanged?.Invoke(
            this,
            new MonitorStatusEventArgs(
                _watchItems.Count == 0
                    ? "请添加需要监控的标的"
                    : $"正在监控 {_watchItems.Count} 个标的"));
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default) =>
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);

    public AnalysisSnapshot? GetAnalysis(InstrumentId instrument, Timeframe timeframe) =>
        _analysis.GetValueOrDefault((instrument, timeframe));

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is not null)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
        }

        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation?.Dispose();
        _pollGate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                StatusChanged?.Invoke(
                    this,
                    new MonitorStatusEventArgs($"监控循环异常：{exception.Message}", true));
            }

            var delay = IsMarketWindow(_clock.Now)
                ? TimeSpan.FromSeconds(_settings.RefreshSeconds)
                : TimeSpan.FromSeconds(60);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _pollGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var now = _clock.Now;
            if (_watchItems.Count == 0)
            {
                return;
            }

            var quoteResult = await _provider
                .GetQuotesAsync(_watchItems.Select(x => x.Instrument).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            if (!quoteResult.IsSuccess)
            {
                StatusChanged?.Invoke(
                    this,
                    new MonitorStatusEventArgs(
                        $"行情暂不可用：{quoteResult.Error ?? "未知错误"}",
                        true));
                return;
            }

            var staleAfter = TimeSpan.FromSeconds(Math.Max(_settings.RefreshSeconds * 2, 10));
            var normalizedQuotes = quoteResult.Data
                .Select(quote =>
                {
                    var refreshed = quote.WithFreshness(now, staleAfter);
                    if (IsMarketWindow(now)
                        && DateOnly.FromDateTime(refreshed.SourceTime.LocalDateTime)
                           != DateOnly.FromDateTime(now.LocalDateTime))
                    {
                        refreshed = refreshed with { Quality = MarketDataQuality.Stale };
                    }

                    if (quoteResult.Quality != MarketDataQuality.Healthy)
                    {
                        refreshed = refreshed with { Quality = quoteResult.Quality };
                    }

                    return refreshed;
                })
                .ToArray();

            foreach (var quote in normalizedQuotes)
            {
                _quotes[quote.Instrument] = quote;
            }

            QuotesUpdated?.Invoke(this, new QuoteBatchEventArgs(normalizedQuotes));
            await EvaluateQuoteAlertsAsync(normalizedQuotes, now, cancellationToken).ConfigureAwait(false);

            var completedMinute = new DateTimeOffset(
                now.Year,
                now.Month,
                now.Day,
                now.Hour,
                now.Minute,
                0,
                now.Offset);
            if (_lastAnalysisMinute != completedMinute)
            {
                _lastAnalysisMinute = completedMinute;
                await AnalyzeWatchItemsAsync(now, cancellationToken).ConfigureAwait(false);
            }

            StatusChanged?.Invoke(
                this,
                new MonitorStatusEventArgs(
                    normalizedQuotes.All(x => x.Quality == MarketDataQuality.Healthy)
                        ? $"行情正常 · {now:HH:mm:ss}"
                        : $"行情受限，已暂停相关信号 · {now:HH:mm:ss}",
                    normalizedQuotes.Any(x => x.Quality is MarketDataQuality.Divergent
                        or MarketDataQuality.Unavailable)));
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task AnalyzeWatchItemsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var items = _watchItems
            .Where(x => x.AnalysisEnabled)
            .Take(_settings.MaximumAnalysisItems)
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        using var concurrency = new SemaphoreSlim(4, 4);

        var tasks = items.Select(async item =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await AnalyzeItemAsync(item, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task AnalyzeItemAsync(
        WatchItem item,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var minuteResult = await _provider
            .GetBarsAsync(item.Instrument, Timeframe.Minute1, 500, cancellationToken)
            .ConfigureAwait(false);
        if (!minuteResult.IsSuccess || minuteResult.Data.Count == 0)
        {
            return;
        }

        var allBars = new List<Bar>(minuteResult.Data);
        foreach (var timeframe in new[]
                 {
                     Timeframe.Minute1,
                     Timeframe.Minute5,
                     Timeframe.Minute15
                 })
        {
            var bars = timeframe == Timeframe.Minute1
                ? minuteResult.Data
                : BarAggregator.Aggregate(minuteResult.Data, timeframe);
            allBars.AddRange(timeframe == Timeframe.Minute1 ? [] : bars);
            var snapshot = _analysisEngine.Analyze(item.Instrument, timeframe, bars, now);
            _analysis[(item.Instrument, timeframe)] = snapshot;
            AnalysisUpdated?.Invoke(this, new AnalysisEventArgs(snapshot));
            await EvaluateAnalysisAlertsAsync(snapshot, now, cancellationToken).ConfigureAwait(false);
        }

        var hourlyResult = await _provider
            .GetBarsAsync(item.Instrument, Timeframe.Minute60, 300, cancellationToken)
            .ConfigureAwait(false);
        if (hourlyResult.IsSuccess && hourlyResult.Data.Count > 0)
        {
            allBars.AddRange(hourlyResult.Data);
            var hourly = _analysisEngine.Analyze(
                item.Instrument,
                Timeframe.Minute60,
                hourlyResult.Data,
                now);
            _analysis[(item.Instrument, Timeframe.Minute60)] = hourly;
            AnalysisUpdated?.Invoke(this, new AnalysisEventArgs(hourly));
        }

        var dailyResult = await _provider
            .GetBarsAsync(item.Instrument, Timeframe.Day, 300, cancellationToken)
            .ConfigureAwait(false);
        if (dailyResult.IsSuccess && dailyResult.Data.Count > 0)
        {
            allBars.AddRange(dailyResult.Data);
            var daily = _analysisEngine.Analyze(
                item.Instrument,
                Timeframe.Day,
                dailyResult.Data,
                now);
            _analysis[(item.Instrument, Timeframe.Day)] = daily;
            AnalysisUpdated?.Invoke(this, new AnalysisEventArgs(daily));
        }

        await _repository.SaveBarsAsync(allBars, cancellationToken).ConfigureAwait(false);
    }

    private async Task EvaluateQuoteAlertsAsync(
        IReadOnlyList<QuoteSnapshot> quotes,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var quote in quotes)
        {
            foreach (var alert in _alertEvaluator.EvaluateQuote(quote, _alertRules, now))
            {
                await RaiseAlertAsync(alert, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EvaluateAnalysisAlertsAsync(
        AnalysisSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!_quotes.TryGetValue(snapshot.Instrument, out var quote)
            || quote.Quality != MarketDataQuality.Healthy)
        {
            return;
        }

        foreach (var alert in _alertEvaluator.EvaluateAnalysis(snapshot, _alertRules, now))
        {
            await RaiseAlertAsync(alert, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RaiseAlertAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        if (await _repository.TryAddAlertEventAsync(alert, cancellationToken).ConfigureAwait(false))
        {
            AlertRaised?.Invoke(this, new AlertEventArgs(alert));
        }
    }

    private static bool IsMarketWindow(DateTimeOffset now)
    {
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(now.LocalDateTime);
        return time >= new TimeOnly(9, 15) && time <= new TimeOnly(11, 31)
            || time >= new TimeOnly(13, 0) && time <= new TimeOnly(15, 5);
    }
}
