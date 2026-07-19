using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Application.Analysis;

public sealed class QuoteFlowTracker
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(3);
    private readonly Lock _gate = new();
    private readonly Dictionary<InstrumentId, List<QuoteFlowSample>> _samples = [];

    public void Observe(IEnumerable<QuoteSnapshot> quotes)
    {
        ArgumentNullException.ThrowIfNull(quotes);
        lock (_gate)
        {
            foreach (var quote in quotes)
            {
                if (quote.Price <= 0 || quote.Volume < 0)
                {
                    continue;
                }

                var observedAt = quote.ReceivedTime;
                if (!_samples.TryGetValue(quote.Instrument, out var samples))
                {
                    samples = [];
                    _samples[quote.Instrument] = samples;
                }

                var currentDate = DateOnly.FromDateTime(quote.SourceTime.LocalDateTime);
                samples.RemoveAll(sample =>
                    DateOnly.FromDateTime(sample.SourceTime.LocalDateTime) != currentDate
                    || observedAt - sample.ObservedAt > Retention);

                if (samples.Count > 0 && samples[^1].ObservedAt == observedAt)
                {
                    samples[^1] = new QuoteFlowSample(
                        observedAt,
                        quote.SourceTime,
                        quote.Price,
                        quote.Volume);
                }
                else
                {
                    samples.Add(new QuoteFlowSample(
                        observedAt,
                        quote.SourceTime,
                        quote.Price,
                        quote.Volume));
                }

                if (samples.Count > 200)
                {
                    samples.RemoveRange(0, samples.Count - 200);
                }
            }
        }
    }

    public QuoteFlowMetrics GetMetrics(InstrumentId instrument)
    {
        lock (_gate)
        {
            if (!_samples.TryGetValue(instrument, out var samples) || samples.Count < 4)
            {
                return QuoteFlowMetrics.WarmingUp;
            }

            var latest = samples[^1];
            var shortStart = FindAtOrBefore(samples, latest.ObservedAt - TimeSpan.FromSeconds(30));
            if (shortStart is null)
            {
                shortStart = samples[0];
            }

            var shortSeconds = (decimal)(latest.ObservedAt - shortStart.ObservedAt).TotalSeconds;
            var shortVolume = latest.Volume - shortStart.Volume;
            if (shortSeconds < 15 || shortVolume < 0 || shortStart.Price <= 0)
            {
                return QuoteFlowMetrics.WarmingUp with { SampleCount = samples.Count };
            }

            var previousStart = FindAtOrBefore(
                samples,
                shortStart.ObservedAt - TimeSpan.FromSeconds(60));
            var shortRate = shortVolume / shortSeconds;
            decimal acceleration = 1;
            if (previousStart is not null)
            {
                var previousSeconds =
                    (decimal)(shortStart.ObservedAt - previousStart.ObservedAt).TotalSeconds;
                var previousVolume = shortStart.Volume - previousStart.Volume;
                if (previousSeconds >= 15 && previousVolume > 0)
                {
                    acceleration = shortRate / (previousVolume / previousSeconds);
                }
            }

            acceleration = Math.Clamp(acceleration, 0, 9.99m);
            return new QuoteFlowMetrics(
                (latest.Price / shortStart.Price - 1m) * 100m,
                shortRate * 60m,
                acceleration,
                samples.Count,
                true);
        }
    }

    private static QuoteFlowSample? FindAtOrBefore(
        IReadOnlyList<QuoteFlowSample> samples,
        DateTimeOffset target)
    {
        for (var index = samples.Count - 1; index >= 0; index--)
        {
            if (samples[index].ObservedAt <= target)
            {
                return samples[index];
            }
        }

        return null;
    }

    private sealed record QuoteFlowSample(
        DateTimeOffset ObservedAt,
        DateTimeOffset SourceTime,
        decimal Price,
        long Volume);
}
