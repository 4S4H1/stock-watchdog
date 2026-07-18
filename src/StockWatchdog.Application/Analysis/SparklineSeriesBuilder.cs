using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Application.Analysis;

public enum SparklineDirection
{
    Flat,
    Rising,
    Falling
}

public sealed record SparklineSeries(
    IReadOnlyList<decimal> Values,
    SparklineDirection Direction)
{
    public static SparklineSeries Empty { get; } =
        new([], SparklineDirection.Flat);
}

public static class SparklineSeriesBuilder
{
    public const int DefaultCapacity = 120;

    public static SparklineSeries Build(
        AnalysisSnapshot snapshot,
        int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Timeframe != Timeframe.Minute1 || capacity <= 0)
        {
            return SparklineSeries.Empty;
        }

        var values = snapshot.Bars
            .Where(bar =>
                bar.IsFinal
                && bar.Instrument == snapshot.Instrument
                && bar.Timeframe == Timeframe.Minute1
                && BarIntegrity.HasValidPrices(bar))
            .TakeLast(capacity)
            .Select(bar => bar.Close)
            .ToArray();
        if (values.Length == 0)
        {
            return SparklineSeries.Empty;
        }

        var direction = values[^1].CompareTo(values[0]) switch
        {
            > 0 => SparklineDirection.Rising,
            < 0 => SparklineDirection.Falling,
            _ => SparklineDirection.Flat
        };
        return new SparklineSeries(values, direction);
    }
}
