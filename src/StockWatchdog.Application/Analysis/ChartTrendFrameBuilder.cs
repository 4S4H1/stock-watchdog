using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Application.Analysis;

public static class ChartTrendFrameBuilder
{
    public static ChartTrendFrame? Build(
        ChartTradeMarker marker,
        IEnumerable<Bar> bars)
    {
        if (marker.Price <= 0)
        {
            return null;
        }

        var latest = bars
            .Where(bar =>
                bar.IsFinal
                && bar.Instrument == marker.Instrument
                && bar.Timeframe == marker.Timeframe
                && bar.EndTime > marker.Time)
            .OrderBy(bar => bar.EndTime)
            .LastOrDefault();

        if (latest is null || latest.Close <= 0)
        {
            return null;
        }

        var change = latest.Close - marker.Price;
        var changePercent = Math.Round(change / marker.Price * 100m, 4);
        var direction = change > 0
            ? TrendFrameDirection.RisingProfit
            : change < 0
                ? TrendFrameDirection.FallingLoss
                : TrendFrameDirection.Flat;

        return new ChartTrendFrame(
            marker,
            latest.EndTime,
            latest.Close,
            change,
            changePercent,
            direction);
    }
}
