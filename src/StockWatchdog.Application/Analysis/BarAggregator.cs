using StockWatchdog.Domain.Market;

namespace StockWatchdog.Application.Analysis;

public static class BarAggregator
{
    private static readonly TimeOnly MorningStart = new(9, 30);
    private static readonly TimeOnly MorningEnd = new(11, 30);
    private static readonly TimeOnly AfternoonStart = new(13, 0);
    private static readonly TimeOnly AfternoonEnd = new(15, 0);

    public static IReadOnlyList<Bar> Aggregate(IReadOnlyList<Bar> minuteBars, Timeframe target)
    {
        if (target == Timeframe.Minute1)
        {
            return minuteBars.OrderBy(x => x.StartTime).ToArray();
        }

        if (target == Timeframe.Day)
        {
            return AggregateDaily(minuteBars);
        }

        var minutes = (int)target;
        if (minutes is not (5 or 15 or 60))
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "不支持的聚合周期");
        }

        return minuteBars
            .Where(x => x.Timeframe == Timeframe.Minute1
                        && IsRegularSession(x.StartTime)
                        && BarIntegrity.HasValidPrices(x))
            .OrderBy(x => x.StartTime)
            .GroupBy(x => GetBucketStart(x.StartTime, minutes))
            .Where(x => x.Key is not null)
            .Select(group => BuildBar(group.Key!.Value, TimeSpan.FromMinutes(minutes), target, group))
            .ToArray();
    }

    private static IReadOnlyList<Bar> AggregateDaily(IReadOnlyList<Bar> minuteBars) =>
        minuteBars
            .Where(x => x.Timeframe == Timeframe.Minute1
                        && IsRegularSession(x.StartTime)
                        && BarIntegrity.HasValidPrices(x))
            .OrderBy(x => x.StartTime)
            .GroupBy(x => DateOnly.FromDateTime(x.StartTime.LocalDateTime))
            .Select(group =>
            {
                var first = group.First();
                var localStart = group.Key.ToDateTime(MorningStart);
                var start = new DateTimeOffset(localStart, first.StartTime.Offset);
                return BuildBar(start, TimeSpan.FromHours(5.5), Timeframe.Day, group);
            })
            .ToArray();

    private static Bar BuildBar(
        DateTimeOffset start,
        TimeSpan duration,
        Timeframe timeframe,
        IEnumerable<Bar> source)
    {
        var bars = source.OrderBy(x => x.StartTime).ToArray();
        var last = bars[^1];
        var expectedEnd = timeframe == Timeframe.Day
            ? new DateTimeOffset(start.Date.AddHours(15), start.Offset)
            : start + duration;
        var expectedMinuteCount = timeframe == Timeframe.Day
            ? 240
            : (int)duration.TotalMinutes;
        var distinctMinutes = bars
            .Select(x => x.StartTime)
            .Distinct()
            .Count();
        var isComplete = bars.All(x => x.IsFinal)
            && bars[0].StartTime == start
            && last.EndTime >= expectedEnd
            && distinctMinutes >= expectedMinuteCount;

        return new Bar(
            bars[0].Instrument,
            timeframe,
            start,
            expectedEnd,
            bars[0].Open,
            bars.Max(x => x.High),
            bars.Min(x => x.Low),
            last.Close,
            bars.Sum(x => x.Volume),
            bars.Sum(x => x.Turnover),
            isComplete,
            last.Source,
            bars.Aggregate(DataQualityFlags.None, (flags, bar) => flags | bar.QualityFlags));
    }

    private static DateTimeOffset? GetBucketStart(DateTimeOffset time, int minutes)
    {
        var local = time.LocalDateTime;
        var timeOnly = TimeOnly.FromDateTime(local);
        TimeOnly sessionStart;
        if (timeOnly >= MorningStart && timeOnly < MorningEnd)
        {
            sessionStart = MorningStart;
        }
        else if (timeOnly >= AfternoonStart && timeOnly < AfternoonEnd)
        {
            sessionStart = AfternoonStart;
        }
        else
        {
            return null;
        }

        var elapsed = (int)(timeOnly.ToTimeSpan() - sessionStart.ToTimeSpan()).TotalMinutes;
        var bucketMinutes = elapsed / minutes * minutes;
        var bucket = local.Date + sessionStart.ToTimeSpan() + TimeSpan.FromMinutes(bucketMinutes);
        return new DateTimeOffset(bucket, time.Offset);
    }

    private static bool IsRegularSession(DateTimeOffset time)
    {
        var local = TimeOnly.FromDateTime(time.LocalDateTime);
        return local >= MorningStart && local < MorningEnd
            || local >= AfternoonStart && local < AfternoonEnd;
    }
}
