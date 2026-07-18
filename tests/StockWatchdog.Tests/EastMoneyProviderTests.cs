using System.Net;
using System.Net.Http;
using System.Text;
using StockWatchdog.Domain.Market;
using StockWatchdog.Infrastructure.MarketData;

namespace StockWatchdog.Tests;

public sealed class EastMoneyProviderTests
{
    [Fact]
    public async Task Hourly_kline_is_parsed_as_a_completed_end_timestamp_bar()
    {
        const string json =
            """
            {
              "data": {
                "klines": [
                  "2026-07-17 10:30,10.00,10.20,10.30,9.90,1234,1250000,0,0,0,0"
                ]
              }
            }
            """;
        var handler = new FixtureHandler(json);
        var provider = new EastMoneyMarketDataProvider(new HttpClient(handler));

        var result = await provider.GetBarsAsync(
            TestData.Stock,
            Timeframe.Minute60,
            30,
            CancellationToken.None);
        var bar = Assert.Single(result.Data);

        Assert.True(result.IsSuccess);
        Assert.Contains("klt=60", handler.LastUri?.Query, StringComparison.Ordinal);
        Assert.Equal(TestData.At(9, 30), bar.StartTime);
        Assert.Equal(TestData.At(10, 30), bar.EndTime);
        Assert.Equal(123_400, bar.Volume);
        Assert.True(bar.IsFinal);
    }

    [Fact]
    public async Task Batch_quote_parser_maps_exchange_price_and_source_time()
    {
        const string json =
            """
            {
              "data": {
                "diff": [
                  {
                    "f2": 1500.25,
                    "f3": 1.25,
                    "f4": 18.50,
                    "f5": 1234,
                    "f6": 185000000,
                    "f12": "600519",
                    "f13": 1,
                    "f14": "贵州茅台",
                    "f18": 1481.75,
                    "f124": 1784253600
                  }
                ]
              }
            }
            """;
        var provider = new EastMoneyMarketDataProvider(
            new HttpClient(new FixtureHandler(json)));

        var result = await provider.GetQuotesAsync([TestData.Stock], CancellationToken.None);
        var quote = Assert.Single(result.Data);

        Assert.True(result.IsSuccess);
        Assert.Equal(TestData.Stock, quote.Instrument);
        Assert.Equal("贵州茅台", quote.Name);
        Assert.Equal(1500.25m, quote.Price);
        Assert.Equal(123_400, quote.Volume);
        Assert.False(quote.QualityFlags.HasFlag(DataQualityFlags.MissingTimestamp));
    }

    [Fact]
    public async Task Etf_minute_rows_with_zero_open_are_recovered_from_price_continuity()
    {
        const string json =
            """
            {
              "data": {
                "trends": [
                  "2026-07-13 09:30,0.000,4.802,4.802,4.802,70930,34060586.000,4.8020",
                  "2026-07-13 09:31,0.000,4.810,4.812,4.801,148427,71363615.000,4.8061",
                  "2026-07-13 09:32,0.000,4.818,4.820,4.809,92296,44410129.000,4.8077"
                ]
              }
            }
            """;
        var instrument = new InstrumentId(Exchange.Shanghai, "510300", AssetType.Etf);
        var provider = new EastMoneyMarketDataProvider(
            new HttpClient(new FixtureHandler(json)));

        var result = await provider.GetBarsAsync(
            instrument,
            Timeframe.Minute1,
            100,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Data.Count);
        Assert.Equal(4.802m, result.Data[0].Open);
        Assert.Equal(4.802m, result.Data[1].Open);
        Assert.Equal(4.810m, result.Data[2].Open);
        Assert.All(
            result.Data,
            bar =>
            {
                Assert.True(BarIntegrity.HasValidPrices(bar));
                Assert.InRange(bar.Open, bar.Low, bar.High);
                Assert.InRange(bar.Close, bar.Low, bar.High);
                Assert.True(bar.QualityFlags.HasFlag(DataQualityFlags.Corrected));
            });
    }

    private sealed class FixtureHandler(string content) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
