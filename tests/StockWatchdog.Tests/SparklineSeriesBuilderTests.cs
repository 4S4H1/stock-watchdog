using StockWatchdog.Application.Analysis;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Tests;

public sealed class SparklineSeriesBuilderTests
{
    [Fact]
    public void Sparkline_uses_the_same_last_120_completed_one_minute_closes_as_detail_chart()
    {
        var bars = Enumerable.Range(0, 130)
            .Select(index => TestData.MinuteBar(
                TestData.At(9, 30).AddMinutes(index),
                10m + index * 0.01m))
            .Append(TestData.MinuteBar(TestData.At(14, 59), 99m, isFinal: false))
            .ToArray();
        var snapshot = Snapshot(bars);

        var series = SparklineSeriesBuilder.Build(snapshot);

        var expected = bars
            .Where(bar => bar.IsFinal && BarIntegrity.HasValidPrices(bar))
            .TakeLast(120)
            .Select(bar => bar.Close);
        Assert.Equal(expected, series.Values);
        Assert.Equal(SparklineDirection.Rising, series.Direction);
    }

    [Fact]
    public void Falling_direction_is_derived_from_the_visible_kline_closes_not_quote_change()
    {
        var bars = Enumerable.Range(0, 8)
            .Select(index => TestData.MinuteBar(
                TestData.At(10, 0).AddMinutes(index),
                12m - index * 0.1m))
            .ToArray();

        var series = SparklineSeriesBuilder.Build(Snapshot(bars));

        Assert.Equal(SparklineDirection.Falling, series.Direction);
        Assert.Equal(bars.Select(bar => bar.Close), series.Values);
    }

    [Fact]
    public void Non_minute_snapshot_does_not_feed_the_intraday_sparkline()
    {
        var bar = new Bar(
            TestData.Stock,
            Timeframe.Minute5,
            TestData.At(10, 0),
            TestData.At(10, 5),
            10m,
            10.2m,
            9.9m,
            10.1m,
            1_000,
            10_100m,
            true,
            "test");

        var series = SparklineSeriesBuilder.Build(
            Snapshot([bar], Timeframe.Minute5));

        Assert.Empty(series.Values);
        Assert.Equal(SparklineDirection.Flat, series.Direction);
    }

    private static AnalysisSnapshot Snapshot(
        IReadOnlyList<Bar> bars,
        Timeframe timeframe = Timeframe.Minute1) =>
        new(
            TestData.Stock,
            timeframe,
            MarketRegime.Ranging,
            bars,
            [],
            [],
            TestData.At(15, 0),
            MarketDataQuality.Healthy,
            "test");
}
