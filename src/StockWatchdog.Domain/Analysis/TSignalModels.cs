using StockWatchdog.Domain.Market;

namespace StockWatchdog.Domain.Analysis;

public enum TScoreSide
{
    Buy,
    Sell
}

public enum TSignalState
{
    Unavailable,
    Wait,
    WatchBuy,
    BuyCandidate,
    WatchSell,
    SellCandidate,
    Volatile
}

public sealed record QuoteFlowMetrics(
    decimal PriceChangePercent,
    decimal VolumePerMinute,
    decimal VolumeAcceleration,
    int SampleCount,
    bool IsReliable)
{
    public static QuoteFlowMetrics WarmingUp { get; } = new(0, 0, 1, 0, false);
}

public sealed record TScoreEvidence(
    TScoreSide Side,
    string Factor,
    int Points,
    string Description);

public sealed record TSignalSnapshot(
    InstrumentId Instrument,
    int? BuyScore,
    int? SellScore,
    TSignalState State,
    string Summary,
    DateTimeOffset CalculatedAt,
    DateTimeOffset ValidUntil,
    MarketDataQuality DataQuality,
    IReadOnlyList<TScoreEvidence> Evidence)
{
    public bool IsAvailable =>
        BuyScore is not null
        && SellScore is not null
        && DataQuality == MarketDataQuality.Healthy;

    public static TSignalSnapshot Unavailable(
        InstrumentId instrument,
        DateTimeOffset now,
        MarketDataQuality quality,
        string summary) =>
        new(
            instrument,
            null,
            null,
            TSignalState.Unavailable,
            summary,
            now,
            now + TimeSpan.FromSeconds(30),
            quality,
            []);
}
