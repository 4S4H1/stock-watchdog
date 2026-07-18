using StockWatchdog.Application.Abstractions;
using StockWatchdog.Domain.Market;
using StockWatchdog.Infrastructure.MarketData;

namespace StockWatchdog.Tests;

public sealed class ResilientProviderTests
{
    [Fact]
    public async Task Matching_primary_and_secondary_quotes_remain_healthy()
    {
        var now = TestData.At(10, 0);
        var provider = Provider(
            Result(Quote(100m, now), "primary", now),
            Result(Quote(100.1m, now), "fallback", now));

        var result = await provider.GetQuotesAsync([TestData.Stock], CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(MarketDataQuality.Healthy, Assert.Single(result.Data).Quality);
    }

    [Fact]
    public async Task Significant_price_conflict_marks_the_quote_divergent()
    {
        var now = TestData.At(10, 0);
        var provider = Provider(
            Result(Quote(100m, now), "primary", now),
            Result(Quote(101m, now), "fallback", now));

        var result = await provider.GetQuotesAsync([TestData.Stock], CancellationToken.None);
        var quote = Assert.Single(result.Data);

        Assert.Equal(MarketDataQuality.Divergent, quote.Quality);
        Assert.True(quote.QualityFlags.HasFlag(DataQualityFlags.SourceMismatch));
    }

    [Fact]
    public async Task Missing_secondary_verification_pauses_signal_quality()
    {
        var now = TestData.At(10, 0);
        var provider = Provider(
            Result(Quote(100m, now), "primary", now),
            ProviderResult<IReadOnlyList<QuoteSnapshot>>.Failure(
                [],
                "fallback",
                now,
                "offline"));

        var result = await provider.GetQuotesAsync([TestData.Stock], CancellationToken.None);
        var quote = Assert.Single(result.Data);

        Assert.Equal(MarketDataQuality.Delayed, quote.Quality);
        Assert.True(quote.QualityFlags.HasFlag(DataQualityFlags.SecondaryUnverified));
    }

    private static ResilientMarketDataProvider Provider(
        ProviderResult<IReadOnlyList<QuoteSnapshot>> primary,
        ProviderResult<IReadOnlyList<QuoteSnapshot>> fallback) =>
        new(new StubProvider("primary", primary), new StubProvider("fallback", fallback));

    private static ProviderResult<IReadOnlyList<QuoteSnapshot>> Result(
        QuoteSnapshot quote,
        string source,
        DateTimeOffset now) =>
        ProviderResult<IReadOnlyList<QuoteSnapshot>>.Success([quote], source, now);

    private static QuoteSnapshot Quote(decimal price, DateTimeOffset now) =>
        new(
            TestData.Stock,
            "测试股票",
            price,
            99m,
            price - 99m,
            0m,
            1_000,
            100_000m,
            now,
            now,
            "fixture",
            MarketDataQuality.Healthy);

    private sealed class StubProvider(
        string name,
        ProviderResult<IReadOnlyList<QuoteSnapshot>> quoteResult) : IMarketDataProvider
    {
        public string Name { get; } = name;

        public ProviderCapabilities Capabilities => ProviderCapabilities.Quotes;

        public Task<ProviderResult<IReadOnlyList<QuoteSnapshot>>> GetQuotesAsync(
            IReadOnlyCollection<InstrumentId> instruments,
            CancellationToken cancellationToken) =>
            Task.FromResult(quoteResult);

        public Task<ProviderResult<IReadOnlyList<Bar>>> GetBarsAsync(
            InstrumentId instrument,
            Timeframe timeframe,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<IReadOnlyList<Bar>>.Failure(
                [],
                Name,
                DateTimeOffset.Now,
                "not supported"));
    }
}
