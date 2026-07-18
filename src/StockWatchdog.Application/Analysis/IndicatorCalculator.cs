using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Application.Analysis;

public static class IndicatorCalculator
{
    public static IReadOnlyList<IndicatorPoint> Calculate(IReadOnlyList<Bar> source)
    {
        var bars = source.OrderBy(x => x.StartTime).ToArray();
        if (bars.Length == 0)
        {
            return [];
        }

        var closes = bars.Select(x => x.Close).ToArray();
        var ema5 = Ema(closes, 5);
        var ema10 = Ema(closes, 10);
        var ema12 = Ema(closes, 12);
        var ema20 = Ema(closes, 20);
        var ema26 = Ema(closes, 26);
        var atr14 = Atr(bars, 14);
        var rsi14 = Rsi(closes, 14);
        var macd = new decimal?[bars.Length];

        for (var index = 0; index < bars.Length; index++)
        {
            if (ema12[index] is { } fast && ema26[index] is { } slow)
            {
                macd[index] = fast - slow;
            }
        }

        var macdSignal = Ema(macd, 9);
        var bollinger = Bollinger(closes, 20, 2m);
        var vwap = Vwap(bars);
        var volumeRatio = VolumeRatio(bars, 20);

        var result = new IndicatorPoint[bars.Length];
        for (var index = 0; index < bars.Length; index++)
        {
            result[index] = new IndicatorPoint(
                bars[index].EndTime,
                closes[index],
                vwap[index],
                ema5[index],
                ema10[index],
                ema20[index],
                atr14[index],
                rsi14[index],
                macd[index],
                macdSignal[index],
                bollinger.Upper[index],
                bollinger.Middle[index],
                bollinger.Lower[index],
                volumeRatio[index]);
        }

        return result;
    }

    private static decimal?[] Ema(IReadOnlyList<decimal> values, int period)
    {
        var nullable = values.Select<decimal, decimal?>(x => x).ToArray();
        return Ema(nullable, period);
    }

    private static decimal?[] Ema(IReadOnlyList<decimal?> values, int period)
    {
        var result = new decimal?[values.Count];
        var seed = new List<decimal>(period);
        decimal? previous = null;
        var multiplier = 2m / (period + 1m);

        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] is not { } value)
            {
                continue;
            }

            if (previous is null)
            {
                seed.Add(value);
                if (seed.Count == period)
                {
                    previous = seed.Average();
                    result[index] = previous;
                }

                continue;
            }

            previous = (value - previous.Value) * multiplier + previous.Value;
            result[index] = previous;
        }

        return result;
    }

    private static decimal?[] Atr(IReadOnlyList<Bar> bars, int period)
    {
        var result = new decimal?[bars.Count];
        var trueRanges = new decimal[bars.Count];

        for (var index = 0; index < bars.Count; index++)
        {
            var highLow = bars[index].High - bars[index].Low;
            if (index == 0)
            {
                trueRanges[index] = highLow;
                continue;
            }

            var highClose = Math.Abs(bars[index].High - bars[index - 1].Close);
            var lowClose = Math.Abs(bars[index].Low - bars[index - 1].Close);
            trueRanges[index] = Math.Max(highLow, Math.Max(highClose, lowClose));
        }

        if (bars.Count < period)
        {
            return result;
        }

        var previous = trueRanges.Take(period).Average();
        result[period - 1] = previous;
        for (var index = period; index < bars.Count; index++)
        {
            previous = (previous * (period - 1) + trueRanges[index]) / period;
            result[index] = previous;
        }

        return result;
    }

    private static decimal?[] Rsi(IReadOnlyList<decimal> closes, int period)
    {
        var result = new decimal?[closes.Count];
        if (closes.Count <= period)
        {
            return result;
        }

        decimal gains = 0;
        decimal losses = 0;
        for (var index = 1; index <= period; index++)
        {
            var change = closes[index] - closes[index - 1];
            gains += Math.Max(change, 0);
            losses += Math.Max(-change, 0);
        }

        var averageGain = gains / period;
        var averageLoss = losses / period;
        result[period] = ToRsi(averageGain, averageLoss);

        for (var index = period + 1; index < closes.Count; index++)
        {
            var change = closes[index] - closes[index - 1];
            averageGain = (averageGain * (period - 1) + Math.Max(change, 0)) / period;
            averageLoss = (averageLoss * (period - 1) + Math.Max(-change, 0)) / period;
            result[index] = ToRsi(averageGain, averageLoss);
        }

        return result;
    }

    private static decimal ToRsi(decimal averageGain, decimal averageLoss)
    {
        if (averageLoss == 0)
        {
            return averageGain == 0 ? 50m : 100m;
        }

        var relativeStrength = averageGain / averageLoss;
        return 100m - 100m / (1m + relativeStrength);
    }

    private static (decimal?[] Upper, decimal?[] Middle, decimal?[] Lower) Bollinger(
        IReadOnlyList<decimal> closes,
        int period,
        decimal deviations)
    {
        var upper = new decimal?[closes.Count];
        var middle = new decimal?[closes.Count];
        var lower = new decimal?[closes.Count];

        for (var index = period - 1; index < closes.Count; index++)
        {
            var window = closes.Skip(index - period + 1).Take(period).ToArray();
            var average = window.Average();
            var variance = window.Sum(value => (value - average) * (value - average)) / period;
            var standardDeviation = (decimal)Math.Sqrt((double)variance);
            middle[index] = average;
            upper[index] = average + deviations * standardDeviation;
            lower[index] = average - deviations * standardDeviation;
        }

        return (upper, middle, lower);
    }

    private static decimal?[] Vwap(IReadOnlyList<Bar> bars)
    {
        var result = new decimal?[bars.Count];
        DateOnly? currentDate = null;
        long cumulativeVolume = 0;
        decimal cumulativeTurnover = 0;

        for (var index = 0; index < bars.Count; index++)
        {
            var date = DateOnly.FromDateTime(bars[index].StartTime.LocalDateTime);
            if (date != currentDate)
            {
                currentDate = date;
                cumulativeVolume = 0;
                cumulativeTurnover = 0;
            }

            cumulativeVolume += bars[index].Volume;
            cumulativeTurnover += bars[index].Turnover;
            result[index] = cumulativeVolume > 0
                ? cumulativeTurnover / cumulativeVolume
                : null;
        }

        return result;
    }

    private static decimal?[] VolumeRatio(IReadOnlyList<Bar> bars, int period)
    {
        var result = new decimal?[bars.Count];
        for (var index = period; index < bars.Count; index++)
        {
            var average = bars.Skip(index - period).Take(period).Average(x => (decimal)x.Volume);
            result[index] = average > 0 ? bars[index].Volume / average : null;
        }

        return result;
    }
}
