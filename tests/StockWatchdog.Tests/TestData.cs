using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Tests;

internal static class TestData
{
    public static InstrumentId Stock { get; } =
        new(Exchange.Shanghai, "600519", AssetType.Stock);

    public static DateTimeOffset At(int hour, int minute) =>
        new(2026, 7, 17, hour, minute, 0, TimeSpan.FromHours(8));

    public static Bar MinuteBar(
        DateTimeOffset start,
        decimal close,
        long volume = 1_000,
        bool isFinal = true,
        DataQualityFlags flags = DataQualityFlags.None) =>
        new(
            Stock,
            Timeframe.Minute1,
            start,
            start.AddMinutes(1),
            close - 0.05m,
            close + 0.10m,
            close - 0.10m,
            close,
            volume,
            close * volume,
            isFinal,
            "fixture",
            flags);

    public static PatternFinding Finding(
        DateTimeOffset now,
        PatternDirection direction = PatternDirection.Bullish) =>
        new(
            Stock,
            Timeframe.Minute1,
            direction == PatternDirection.Bearish
                ? "vwap-mean-reversion-high"
                : "vwap-mean-reversion-low",
            "VWAP 过度偏离",
            "test",
            direction,
            80,
            now,
            now.AddMinutes(10),
            true,
            [],
            "固定测试证据",
            "越过失效价",
            [],
            "fixture",
            TimeSpan.Zero,
            DataQualityFlags.None);

    public static AnalysisSnapshot CandidateAnalysis(
        DateTimeOffset now,
        MarketDataQuality quality = MarketDataQuality.Healthy,
        PatternDirection direction = PatternDirection.Bullish) =>
        new(
            Stock,
            Timeframe.Minute1,
            MarketRegime.Ranging,
            [],
            [
                new IndicatorPoint(
                    now,
                    100m,
                    100m,
                    100m,
                    100m,
                    100m,
                    2m,
                    25m,
                    0m,
                    0m,
                    102m,
                    100m,
                    98m,
                    1m)
            ],
            [Finding(now, direction)],
            now,
            quality,
            "fixture");
}
