using StockWatchdog.Application.Abstractions;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Infrastructure.MarketData;

public sealed class ResilientMarketDataProvider : IMarketDataProvider
{
    private readonly Lock _gate = new();
    private readonly IMarketDataProvider _primary;
    private readonly IMarketDataProvider _fallback;
    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenUntil;
    private readonly Dictionary<Timeframe, int> _barFailures = [];
    private readonly Dictionary<Timeframe, DateTimeOffset> _barCircuitOpenUntil = [];

    public ResilientMarketDataProvider(IMarketDataProvider primary, IMarketDataProvider fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public string Name => $"{_primary.Name}+{_fallback.Name}";

    public ProviderCapabilities Capabilities => _primary.Capabilities | _fallback.Capabilities;

    public async Task<ProviderResult<IReadOnlyList<QuoteSnapshot>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentId> instruments,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        bool circuitOpen;
        lock (_gate)
        {
            circuitOpen = now < _circuitOpenUntil;
        }

        if (circuitOpen)
        {
            return await _fallback.GetQuotesAsync(instruments, cancellationToken).ConfigureAwait(false);
        }

        var primaryTask = _primary.GetQuotesAsync(instruments, cancellationToken);
        var fallbackTask = _fallback.GetQuotesAsync(instruments, cancellationToken);
        await Task.WhenAll(primaryTask, fallbackTask).ConfigureAwait(false);
        var primaryResult = await primaryTask.ConfigureAwait(false);
        var fallbackResult = await fallbackTask.ConfigureAwait(false);
        if (primaryResult.IsSuccess)
        {
            lock (_gate)
            {
                _consecutiveFailures = 0;
                _circuitOpenUntil = default;
            }

            return CrossCheck(primaryResult, fallbackResult);
        }

        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3)
            {
                _circuitOpenUntil = now + TimeSpan.FromSeconds(30);
            }
        }

        return fallbackResult;
    }

    public async Task<ProviderResult<IReadOnlyList<Bar>>> GetBarsAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        int limit,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        bool circuitOpen;
        lock (_gate)
        {
            circuitOpen = _barCircuitOpenUntil.GetValueOrDefault(timeframe) > now;
        }

        if (!circuitOpen)
        {
            var primary = await _primary
                .GetBarsAsync(instrument, timeframe, limit, cancellationToken)
                .ConfigureAwait(false);
            if (primary.IsSuccess)
            {
                lock (_gate)
                {
                    _barFailures[timeframe] = 0;
                    _barCircuitOpenUntil.Remove(timeframe);
                }

                return primary;
            }

            lock (_gate)
            {
                var failures = _barFailures.GetValueOrDefault(timeframe) + 1;
                _barFailures[timeframe] = failures;
                if (failures >= 2)
                {
                    _barCircuitOpenUntil[timeframe] = now + TimeSpan.FromMinutes(2);
                }
            }
        }

        return await _fallback
            .GetBarsAsync(instrument, timeframe, limit, cancellationToken)
            .ConfigureAwait(false);
    }

    private static ProviderResult<IReadOnlyList<QuoteSnapshot>> CrossCheck(
        ProviderResult<IReadOnlyList<QuoteSnapshot>> primary,
        ProviderResult<IReadOnlyList<QuoteSnapshot>> fallback)
    {
        if (!fallback.IsSuccess)
        {
            return primary with
            {
                Data = primary.Data
                    .Select(x => x with
                    {
                        Quality = MarketDataQuality.Delayed,
                        QualityFlags = x.QualityFlags | DataQualityFlags.SecondaryUnverified
                    })
                    .ToArray()
            };
        }

        var secondaryByInstrument = fallback.Data.ToDictionary(x => x.Instrument);
        var checkedQuotes = primary.Data
            .Select(quote =>
            {
                if (!secondaryByInstrument.TryGetValue(quote.Instrument, out var secondary)
                    || Math.Abs((quote.SourceTime - secondary.SourceTime).TotalSeconds) > 15)
                {
                    return quote with
                    {
                        Quality = MarketDataQuality.Delayed,
                        QualityFlags = quote.QualityFlags | DataQualityFlags.SecondaryUnverified
                    };
                }

                var relativeDifference = quote.Price <= 0
                    ? decimal.MaxValue
                    : Math.Abs(quote.Price - secondary.Price) / quote.Price;
                return relativeDifference > 0.005m
                    ? quote with
                    {
                        Quality = MarketDataQuality.Divergent,
                        QualityFlags = quote.QualityFlags | DataQualityFlags.SourceMismatch
                    }
                    : quote;
            })
            .ToArray();
        return primary with { Data = checkedQuotes };
    }
}
