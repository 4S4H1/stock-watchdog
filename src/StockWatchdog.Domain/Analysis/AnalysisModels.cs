using StockWatchdog.Domain.Market;

namespace StockWatchdog.Domain.Analysis;

public enum PatternDirection
{
    Neutral,
    Bullish,
    Bearish
}

public enum MarketRegime
{
    Unknown,
    Ranging,
    TrendingUp,
    TrendingDown
}

public sealed record PatternEvidence(
    string Indicator,
    decimal Actual,
    string Comparator,
    decimal Threshold,
    string Unit,
    string Description);

public sealed record PatternFinding(
    InstrumentId Instrument,
    Timeframe Timeframe,
    string PatternId,
    string DisplayName,
    string AnalyzerVersion,
    PatternDirection Direction,
    int Score,
    DateTimeOffset DetectedAt,
    DateTimeOffset ValidUntil,
    bool IsFinal,
    IReadOnlyList<PatternEvidence> Evidence,
    string Rationale,
    string Invalidation,
    IReadOnlyList<string> RelatedBarIds,
    string Source,
    TimeSpan Lag,
    DataQualityFlags DataQualityFlags);

public sealed record IndicatorPoint(
    DateTimeOffset Time,
    decimal Close,
    decimal? Vwap,
    decimal? Ema5,
    decimal? Ema10,
    decimal? Ema20,
    decimal? Atr14,
    decimal? Rsi14,
    decimal? Macd,
    decimal? MacdSignal,
    decimal? BollingerUpper,
    decimal? BollingerMiddle,
    decimal? BollingerLower,
    decimal? VolumeRatio);

public sealed record AnalysisSnapshot(
    InstrumentId Instrument,
    Timeframe Timeframe,
    MarketRegime Regime,
    IReadOnlyList<Bar> Bars,
    IReadOnlyList<IndicatorPoint> Indicators,
    IReadOnlyList<PatternFinding> Findings,
    DateTimeOffset CalculatedAt,
    MarketDataQuality DataQuality,
    string StatusText)
{
    public static AnalysisSnapshot WarmingUp(InstrumentId instrument, Timeframe timeframe, DateTimeOffset now) =>
        new(
            instrument,
            timeframe,
            MarketRegime.Unknown,
            [],
            [],
            [],
            now,
            MarketDataQuality.WarmingUp,
            "指标预热中");
}
