using StockWatchdog.Application.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Tests;

public sealed class MarketAnalysisTests
{
    [Theory]
    [InlineData("600519", Exchange.Shanghai, AssetType.Stock)]
    [InlineData("SZ.000001", Exchange.Shenzhen, AssetType.Stock)]
    [InlineData("510300", Exchange.Shanghai, AssetType.Etf)]
    [InlineData("159915", Exchange.Shenzhen, AssetType.Etf)]
    public void Instrument_parser_normalizes_supported_codes(
        string input,
        Exchange exchange,
        AssetType assetType)
    {
        var parsed = InstrumentId.TryParse(input, out var instrument);

        Assert.True(parsed);
        Assert.Equal(exchange, instrument.Exchange);
        Assert.Equal(assetType, instrument.AssetType);
        Assert.Equal(6, instrument.Code.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("BJ920000")]
    [InlineData("60051A")]
    public void Instrument_parser_rejects_unsupported_or_invalid_codes(string input) =>
        Assert.False(InstrumentId.TryParse(input, out _));

    [Fact]
    public void Five_minute_aggregation_never_crosses_the_lunch_break()
    {
        var bars = Enumerable.Range(0, 5)
            .Select(index => TestData.MinuteBar(TestData.At(11, 25).AddMinutes(index), 10m + index))
            .Concat(Enumerable.Range(0, 5)
                .Select(index => TestData.MinuteBar(TestData.At(13, 0).AddMinutes(index), 20m + index)))
            .ToArray();

        var aggregated = BarAggregator.Aggregate(bars, Timeframe.Minute5);

        Assert.Equal(2, aggregated.Count);
        Assert.Equal(TestData.At(11, 25), aggregated[0].StartTime);
        Assert.Equal(TestData.At(11, 30), aggregated[0].EndTime);
        Assert.Equal(TestData.At(13, 0), aggregated[1].StartTime);
        Assert.Equal(TestData.At(13, 5), aggregated[1].EndTime);
        Assert.All(aggregated, bar => Assert.True(bar.IsFinal));
    }

    [Fact]
    public void Incomplete_bucket_is_not_marked_final()
    {
        var bars = new[]
        {
            TestData.MinuteBar(TestData.At(14, 58), 10m),
            TestData.MinuteBar(TestData.At(14, 59), 10.1m)
        };

        var aggregated = Assert.Single(BarAggregator.Aggregate(bars, Timeframe.Minute5));

        Assert.False(aggregated.IsFinal);
        Assert.Equal(TestData.At(15, 0), aggregated.EndTime);
    }

    [Fact]
    public void Indicators_do_not_change_past_values_when_a_future_bar_is_added()
    {
        var bars = Enumerable.Range(0, 35)
            .Select(index => TestData.MinuteBar(
                TestData.At(9, 30).AddMinutes(index),
                10m + index * 0.03m,
                1_000 + index * 10))
            .ToArray();

        var prefix = IndicatorCalculator.Calculate(bars[..34]);
        var extended = IndicatorCalculator.Calculate(bars);

        Assert.Equal(prefix, extended.Take(34));
    }

    [Fact]
    public void Technical_analysis_uses_only_completed_bars()
    {
        var completed = Enumerable.Range(0, 35)
            .Select(index => TestData.MinuteBar(
                TestData.At(9, 30).AddMinutes(index),
                10m + index * 0.06m,
                1_000 + index * 20))
            .ToArray();
        var incomplete = TestData.MinuteBar(TestData.At(10, 5), 99m, 50_000, false);
        var engine = new TechnicalAnalysisEngine();

        var snapshot = engine.Analyze(
            TestData.Stock,
            Timeframe.Minute1,
            [.. completed, incomplete],
            TestData.At(10, 6));

        Assert.Equal(MarketDataQuality.Healthy, snapshot.DataQuality);
        Assert.Equal(completed.Length, snapshot.Bars.Count);
        Assert.Equal(completed[^1].Close, snapshot.Bars[^1].Close);
        Assert.All(snapshot.Bars, bar => Assert.True(bar.IsFinal));
    }

    [Fact]
    public void Any_bar_quality_flag_suspends_healthy_analysis()
    {
        var bars = Enumerable.Range(0, 35)
            .Select(index => TestData.MinuteBar(
                TestData.At(9, 30).AddMinutes(index),
                10m + index * 0.02m,
                flags: index == 20 ? DataQualityFlags.OutOfOrder : DataQualityFlags.None))
            .ToArray();

        var snapshot = new TechnicalAnalysisEngine().Analyze(
            TestData.Stock,
            Timeframe.Minute1,
            bars,
            TestData.At(10, 6));

        Assert.Equal(MarketDataQuality.Delayed, snapshot.DataQuality);
    }
}
