using StockWatchdog.Application.Analysis;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Tests;

public sealed class ChartTrendFrameBuilderTests
{
    [Fact]
    public void Rising_frame_uses_the_latest_completed_bar_and_is_profitable()
    {
        var marker = new ChartTradeMarker(
            Guid.NewGuid(),
            TestData.Stock,
            Timeframe.Minute1,
            ChartTradeSide.Buy,
            TestData.At(9, 31),
            10m,
            TestData.At(9, 31));
        var bars = new[]
        {
            TestData.MinuteBar(TestData.At(9, 31), 10.1m),
            TestData.MinuteBar(TestData.At(9, 32), 10.3m),
            TestData.MinuteBar(TestData.At(9, 33), 99m, isFinal: false)
        };

        var frame = ChartTrendFrameBuilder.Build(marker, bars);

        Assert.NotNull(frame);
        Assert.Equal(TestData.At(9, 33), frame!.EndTime);
        Assert.Equal(10.3m, frame.EndPrice);
        Assert.Equal(TrendFrameDirection.RisingProfit, frame.Direction);
        Assert.Equal(3m, frame.ChangePercent);
    }

    [Fact]
    public void Falling_frame_is_classified_as_failed_trade_loss()
    {
        var marker = new ChartTradeMarker(
            Guid.NewGuid(),
            TestData.Stock,
            Timeframe.Minute5,
            ChartTradeSide.Sell,
            TestData.At(10, 0),
            20m,
            TestData.At(10, 0));
        var bars = new[]
        {
            NewBar(Timeframe.Minute5, TestData.At(10, 0), 19.6m),
            NewBar(Timeframe.Minute5, TestData.At(10, 5), 19m)
        };

        var frame = ChartTrendFrameBuilder.Build(marker, bars);

        Assert.NotNull(frame);
        Assert.Equal(TrendFrameDirection.FallingLoss, frame!.Direction);
        Assert.Equal(-5m, frame.ChangePercent);
    }

    [Fact]
    public void Bars_from_other_instruments_timeframes_or_before_the_marker_are_ignored()
    {
        var marker = new ChartTradeMarker(
            Guid.NewGuid(),
            TestData.Stock,
            Timeframe.Minute5,
            ChartTradeSide.Buy,
            TestData.At(10, 5),
            10m,
            TestData.At(10, 5));
        var otherInstrument = new InstrumentId(Exchange.Shenzhen, "000001", AssetType.Stock);
        var bars = new[]
        {
            NewBar(Timeframe.Minute5, TestData.At(10, 0), 12m),
            NewBar(Timeframe.Minute1, TestData.At(10, 5), 12m),
            NewBar(Timeframe.Minute5, TestData.At(10, 5), 12m, otherInstrument)
        };

        Assert.Null(ChartTrendFrameBuilder.Build(marker, bars));
    }

    private static Bar NewBar(
        Timeframe timeframe,
        DateTimeOffset start,
        decimal close,
        InstrumentId? instrument = null) =>
        new(
            instrument ?? TestData.Stock,
            timeframe,
            start,
            start.AddMinutes((int)timeframe),
            close,
            close,
            close,
            close,
            1_000,
            close * 1_000,
            true,
            "test");
}
