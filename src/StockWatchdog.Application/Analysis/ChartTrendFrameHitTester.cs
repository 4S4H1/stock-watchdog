using StockWatchdog.Domain.Analysis;

namespace StockWatchdog.Application.Analysis;

public static class ChartTrendFrameHitTester
{
    public static ChartTrendFrame? FindHit(
        IEnumerable<ChartTrendFrame> frames,
        DateTime chartTime,
        decimal price)
    {
        ArgumentNullException.ThrowIfNull(frames);

        return frames
            .Where(frame => Contains(frame, chartTime, price))
            .Select(frame => new
            {
                Frame = frame,
                Area = Area(frame),
                CenterDistance = CenterDistance(frame, chartTime, price)
            })
            .OrderBy(candidate => candidate.Area)
            .ThenBy(candidate => candidate.CenterDistance)
            .ThenByDescending(candidate => candidate.Frame.Marker.CreatedAt)
            .Select(candidate => candidate.Frame)
            .FirstOrDefault();
    }

    private static bool Contains(
        ChartTrendFrame frame,
        DateTime chartTime,
        decimal price)
    {
        var start = frame.Marker.Time.DateTime;
        var end = frame.EndTime.DateTime;
        if (frame.Direction == TrendFrameDirection.Flat || end <= start)
        {
            return false;
        }

        var minimumPrice = Math.Min(frame.Marker.Price, frame.EndPrice);
        var maximumPrice = Math.Max(frame.Marker.Price, frame.EndPrice);
        return maximumPrice > minimumPrice
            && chartTime >= start
            && chartTime <= end
            && price >= minimumPrice
            && price <= maximumPrice;
    }

    private static double Area(ChartTrendFrame frame)
    {
        var seconds = (frame.EndTime - frame.Marker.Time).TotalSeconds;
        var priceRange = (double)Math.Abs(frame.EndPrice - frame.Marker.Price);
        return seconds * priceRange;
    }

    private static double CenterDistance(
        ChartTrendFrame frame,
        DateTime chartTime,
        decimal price)
    {
        var start = frame.Marker.Time.DateTime;
        var durationSeconds = (frame.EndTime.DateTime - start).TotalSeconds;
        var timeCenter = start.AddSeconds(durationSeconds / 2);
        var timeDistance = Math.Abs((chartTime - timeCenter).TotalSeconds)
            / durationSeconds;

        var minimumPrice = Math.Min(frame.Marker.Price, frame.EndPrice);
        var maximumPrice = Math.Max(frame.Marker.Price, frame.EndPrice);
        var priceRange = maximumPrice - minimumPrice;
        var priceCenter = minimumPrice + priceRange / 2;
        var priceDistance = (double)(Math.Abs(price - priceCenter) / priceRange);

        return timeDistance * timeDistance + priceDistance * priceDistance;
    }
}
