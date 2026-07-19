using StockWatchdog.Application.Alerts;
using StockWatchdog.Application.Analysis;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.Tests;

public sealed class TSignalEngineTests
{
    [Fact]
    public void Strong_low_price_reversal_produces_buy_candidate()
    {
        var now = TestData.At(10, 30);
        var snapshots = Snapshots(now, bullish: true);
        var signal = new TSignalEngine().Analyze(
            Quote(95m, now),
            snapshots,
            new QuoteFlowMetrics(0.08m, 100_000m, 2m, 20, true),
            now,
            75);

        Assert.True(signal.IsAvailable);
        Assert.Equal(TSignalState.BuyCandidate, signal.State);
        Assert.True(signal.BuyScore >= 75);
        Assert.True(signal.BuyScore > signal.SellScore);
        Assert.Contains(signal.Evidence, item =>
            item.Side == TScoreSide.Buy && item.Factor == "实时量速");
    }

    [Fact]
    public void Strong_high_price_reversal_produces_sell_candidate()
    {
        var now = TestData.At(10, 30);
        var snapshots = Snapshots(now, bullish: false);
        var signal = new TSignalEngine().Analyze(
            Quote(105m, now),
            snapshots,
            new QuoteFlowMetrics(-0.08m, 100_000m, 2m, 20, true),
            now,
            75);

        Assert.True(signal.IsAvailable);
        Assert.Equal(TSignalState.SellCandidate, signal.State);
        Assert.True(signal.SellScore >= 75);
        Assert.True(signal.SellScore > signal.BuyScore);
    }

    [Fact]
    public void Unhealthy_quote_suspends_both_scores()
    {
        var now = TestData.At(10, 30);
        var quote = Quote(100m, now) with { Quality = MarketDataQuality.Divergent };

        var signal = new TSignalEngine().Analyze(
            quote,
            Snapshots(now, bullish: true),
            QuoteFlowMetrics.WarmingUp,
            now);

        Assert.False(signal.IsAvailable);
        Assert.Null(signal.BuyScore);
        Assert.Null(signal.SellScore);
        Assert.Equal(TSignalState.Unavailable, signal.State);
    }

    [Fact]
    public void Quote_flow_tracker_detects_short_term_volume_acceleration()
    {
        var tracker = new QuoteFlowTracker();
        var now = TestData.At(10, 0);
        var volumes = new long[] { 1_000, 1_100, 1_200, 1_300, 1_400, 1_800, 2_400 };
        for (var index = 0; index < volumes.Length; index++)
        {
            var observedAt = now.AddSeconds(index * 15);
            tracker.Observe(
            [
                Quote(100m + index * 0.02m, observedAt) with
                {
                    Volume = volumes[index],
                    ReceivedTime = observedAt,
                    SourceTime = observedAt
                }
            ]);
        }

        var metrics = tracker.GetMetrics(TestData.Stock);

        Assert.True(metrics.IsReliable);
        Assert.True(metrics.VolumeAcceleration > 2m);
        Assert.True(metrics.VolumePerMinute > 0);
        Assert.True(metrics.PriceChangePercent > 0);
    }

    [Fact]
    public void Score_alert_requires_two_confirmations_and_obeys_cooldown()
    {
        var evaluator = new TScoreAlertEvaluator();
        var now = TestData.At(10, 0);
        var first = Signal(now, 82, 20);
        var second = Signal(now.AddSeconds(5), 84, 22);

        Assert.Empty(evaluator.Evaluate(first, true, 75, TimeSpan.FromMinutes(10), now));
        var alert = Assert.Single(evaluator.Evaluate(
            second,
            true,
            75,
            TimeSpan.FromMinutes(10),
            now.AddSeconds(5)));
        Assert.Equal(AlertRuleType.TScoreBuy, alert.Type);

        Assert.Empty(evaluator.Evaluate(
            Signal(now.AddMinutes(1), 85, 20),
            true,
            75,
            TimeSpan.FromMinutes(10),
            now.AddMinutes(1)));

        _ = evaluator.Evaluate(
            Signal(now.AddMinutes(2), 60, 20),
            true,
            75,
            TimeSpan.FromMinutes(10),
            now.AddMinutes(2));
        Assert.Empty(evaluator.Evaluate(
            Signal(now.AddMinutes(11), 82, 20),
            true,
            75,
            TimeSpan.FromMinutes(10),
            now.AddMinutes(11)));
        Assert.Single(evaluator.Evaluate(
            Signal(now.AddMinutes(11).AddSeconds(5), 83, 20),
            true,
            75,
            TimeSpan.FromMinutes(10),
            now.AddMinutes(11).AddSeconds(5)));
    }

    [Fact]
    public void Legacy_settings_enable_score_defaults_during_normalization()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            """{"refreshSeconds":5,"themeId":"dark"}""",
            new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));

        var normalized = Assert.IsType<AppSettings>(legacy).Normalize();

        Assert.True(normalized.TScoreEnabled);
        Assert.Equal(75, normalized.TScoreAlertThreshold);
        Assert.Equal(10, normalized.TScoreCooldownMinutes);
        Assert.Equal(2, normalized.SettingsSchemaVersion);
    }

    private static IReadOnlyDictionary<Timeframe, AnalysisSnapshot> Snapshots(
        DateTimeOffset now,
        bool bullish)
    {
        var minute1 = Snapshot(Timeframe.Minute1, now, bullish, MarketRegime.Ranging);
        var direction = bullish ? MarketRegime.TrendingUp : MarketRegime.TrendingDown;
        return new Dictionary<Timeframe, AnalysisSnapshot>
        {
            [Timeframe.Minute1] = minute1,
            [Timeframe.Minute5] = Snapshot(Timeframe.Minute5, now, bullish, direction),
            [Timeframe.Minute15] = Snapshot(Timeframe.Minute15, now, bullish, direction),
            [Timeframe.Minute60] = Snapshot(Timeframe.Minute60, now, bullish, direction),
            [Timeframe.Day] = Snapshot(Timeframe.Day, now, bullish, direction)
        };
    }

    private static AnalysisSnapshot Snapshot(
        Timeframe timeframe,
        DateTimeOffset now,
        bool bullish,
        MarketRegime regime)
    {
        var close = bullish ? 95m : 105m;
        var bars = Enumerable.Range(0, 30)
            .Select(index =>
            {
                var start = now.AddMinutes(index - 30);
                var open = index == 29
                    ? bullish ? close - 0.4m : close + 0.4m
                    : close;
                return new Bar(
                    TestData.Stock,
                    timeframe,
                    start,
                    start.AddMinutes(Math.Max(1, (int)timeframe)),
                    open,
                    Math.Max(open, close) + 0.2m,
                    Math.Min(open, close) - 0.2m,
                    close,
                    index == 29 ? 2_000 : 1_000,
                    close * (index == 29 ? 2_000 : 1_000),
                    true,
                    "fixture");
            })
            .ToArray();
        var previous = new IndicatorPoint(
            now.AddMinutes(-1),
            close,
            100m,
            99m,
            100m,
            100m,
            2m,
            bullish ? 20m : 80m,
            bullish ? -0.2m : 0.2m,
            0m,
            104m,
            100m,
            96m,
            1m);
        var latest = new IndicatorPoint(
            now,
            close,
            100m,
            99m,
            100m,
            100m,
            2m,
            bullish ? 25m : 75m,
            bullish ? 0.2m : -0.2m,
            0m,
            104m,
            100m,
            96m,
            2m);
        var finding = TestData.Finding(
            now,
            bullish ? PatternDirection.Bullish : PatternDirection.Bearish) with
        {
            Timeframe = timeframe
        };
        return new AnalysisSnapshot(
            TestData.Stock,
            timeframe,
            regime,
            bars,
            [previous, latest],
            [finding],
            now,
            MarketDataQuality.Healthy,
            "fixture");
    }

    private static QuoteSnapshot Quote(decimal price, DateTimeOffset now) =>
        new(
            TestData.Stock,
            "测试股票",
            price,
            100m,
            price - 100m,
            price - 100m,
            1_000,
            price * 1_000m,
            now,
            now,
            "fixture",
            MarketDataQuality.Healthy);

    private static TSignalSnapshot Signal(DateTimeOffset now, int buy, int sell) =>
        new(
            TestData.Stock,
            buy,
            sell,
            TSignalState.BuyCandidate,
            "低吸候选",
            now,
            now.AddMinutes(2),
            MarketDataQuality.Healthy,
            []);
}
