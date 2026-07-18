using StockWatchdog.Application.Analysis;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Tests;

public sealed class ChartTrendFrameHitTesterTests
{
    [Fact]
    public void Double_click_inside_frame_returns_its_marker()
    {
        var frame = Frame(
            markerTime: TestData.At(10, 0),
            markerPrice: 10m,
            endTime: TestData.At(10, 20),
            endPrice: 11m);

        var hit = ChartTrendFrameHitTester.FindHit(
            [frame],
            TestData.At(10, 10).DateTime,
            10.5m);

        Assert.Equal(frame, hit);
    }

    [Fact]
    public void Double_click_outside_all_frames_does_not_delete_any_marker()
    {
        var frame = Frame(
            markerTime: TestData.At(10, 0),
            markerPrice: 10m,
            endTime: TestData.At(10, 20),
            endPrice: 11m);

        var hit = ChartTrendFrameHitTester.FindHit(
            [frame],
            TestData.At(10, 10).DateTime,
            11.5m);

        Assert.Null(hit);
    }

    [Fact]
    public void Overlapping_frames_prefer_the_smallest_visible_box()
    {
        var large = Frame(
            markerTime: TestData.At(10, 0),
            markerPrice: 9m,
            endTime: TestData.At(10, 30),
            endPrice: 12m);
        var small = Frame(
            markerTime: TestData.At(10, 10),
            markerPrice: 10m,
            endTime: TestData.At(10, 20),
            endPrice: 11m);

        var hit = ChartTrendFrameHitTester.FindHit(
            [large, small],
            TestData.At(10, 15).DateTime,
            10.5m);

        Assert.Equal(small, hit);
    }

    [Fact]
    public void Flat_result_has_no_drawn_box_and_is_not_hit_testable()
    {
        var flat = Frame(
            markerTime: TestData.At(10, 0),
            markerPrice: 10m,
            endTime: TestData.At(10, 20),
            endPrice: 10m,
            TrendFrameDirection.Flat);

        Assert.Null(ChartTrendFrameHitTester.FindHit(
            [flat],
            TestData.At(10, 10).DateTime,
            10m));
    }

    private static ChartTrendFrame Frame(
        DateTimeOffset markerTime,
        decimal markerPrice,
        DateTimeOffset endTime,
        decimal endPrice,
        TrendFrameDirection direction = TrendFrameDirection.RisingProfit)
    {
        var marker = new ChartTradeMarker(
            Guid.NewGuid(),
            TestData.Stock,
            Timeframe.Minute1,
            ChartTradeSide.Buy,
            markerTime,
            markerPrice,
            markerTime);
        var change = endPrice - markerPrice;
        return new ChartTrendFrame(
            marker,
            endTime,
            endPrice,
            change,
            markerPrice == 0 ? 0 : change / markerPrice * 100m,
            direction);
    }
}
