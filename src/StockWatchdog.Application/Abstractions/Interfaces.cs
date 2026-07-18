using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.Application.Abstractions;

public sealed record ProviderResult<T>(
    bool IsSuccess,
    T Data,
    MarketDataQuality Quality,
    string Source,
    DateTimeOffset ReceivedAt,
    string? Error = null)
{
    public static ProviderResult<T> Success(T data, string source, DateTimeOffset receivedAt) =>
        new(true, data, MarketDataQuality.Healthy, source, receivedAt);

    public static ProviderResult<T> Failure(
        T empty,
        string source,
        DateTimeOffset receivedAt,
        string error,
        MarketDataQuality quality = MarketDataQuality.Unavailable) =>
        new(false, empty, quality, source, receivedAt, error);
}

public interface IMarketDataProvider
{
    string Name { get; }

    ProviderCapabilities Capabilities { get; }

    Task<ProviderResult<IReadOnlyList<QuoteSnapshot>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentId> instruments,
        CancellationToken cancellationToken);

    Task<ProviderResult<IReadOnlyList<Bar>>> GetBarsAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        int limit,
        CancellationToken cancellationToken);
}

public interface IAppRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchItem>> GetWatchItemsAsync(CancellationToken cancellationToken = default);

    Task UpsertWatchItemAsync(WatchItem item, CancellationToken cancellationToken = default);

    Task DeleteWatchItemAsync(InstrumentId instrument, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChartTradeMarker>> GetChartTradeMarkersAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        CancellationToken cancellationToken = default);

    Task UpsertChartTradeMarkerAsync(
        ChartTradeMarker marker,
        CancellationToken cancellationToken = default);

    Task DeleteChartTradeMarkerAsync(
        Guid markerId,
        CancellationToken cancellationToken = default);

    Task DeleteChartTradeMarkersAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlertRule>> GetAlertRulesAsync(CancellationToken cancellationToken = default);

    Task UpsertAlertRuleAsync(AlertRule rule, CancellationToken cancellationToken = default);

    Task DeleteAlertRuleAsync(Guid ruleId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlertEvent>> GetAlertEventsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    Task<bool> TryAddAlertEventAsync(
        AlertEvent alert,
        CancellationToken cancellationToken = default);

    Task SaveBarsAsync(IReadOnlyCollection<Bar> bars, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Bar>> GetCachedBarsAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        int limit,
        CancellationToken cancellationToken = default);
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public interface ITechnicalAnalysisEngine
{
    AnalysisSnapshot Analyze(
        InstrumentId instrument,
        Timeframe timeframe,
        IReadOnlyList<Bar> bars,
        DateTimeOffset now);
}

public interface IAlertSink
{
    Task ShowAsync(AlertEvent alert, CancellationToken cancellationToken = default);

    void Silence();
}
