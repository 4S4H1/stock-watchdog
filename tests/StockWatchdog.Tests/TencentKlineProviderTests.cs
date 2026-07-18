using System.Net;
using System.Net.Http;
using System.Text;
using StockWatchdog.Domain.Market;
using StockWatchdog.Infrastructure.MarketData;

namespace StockWatchdog.Tests;

public sealed class TencentKlineProviderTests
{
    [Fact]
    public async Task Hourly_fallback_bars_are_explicitly_degraded()
    {
        const string json =
            """
            {
              "code": 0,
              "data": {
                "sh600519": {
                  "m60": [
                    ["202607171030", "1250.00", "1258.00", "1260.00", "1248.00", "34100.12", "", "27.2"]
                  ]
                }
              }
            }
            """;
        var provider = new TencentSnapshotProvider(new HttpClient(new FixtureHandler(json)));

        var result = await provider.GetBarsAsync(
            TestData.Stock,
            Timeframe.Minute60,
            30,
            CancellationToken.None);
        var bar = Assert.Single(result.Data);

        Assert.Equal(MarketDataQuality.Delayed, result.Quality);
        Assert.Equal(TestData.At(9, 30), bar.StartTime);
        Assert.Equal(TestData.At(10, 30), bar.EndTime);
        Assert.Equal(3_410_012, bar.Volume);
        Assert.True(bar.QualityFlags.HasFlag(DataQualityFlags.ParseFallback));
    }

    [Fact]
    public async Task Adjusted_daily_fallback_bars_are_parsed()
    {
        const string json =
            """
            {
              "code": 0,
              "data": {
                "sh600519": {
                  "qfqday": [
                    ["2026-07-17", "1250.00", "1253.00", "1261.53", "1249.10", "11108.00"]
                  ]
                }
              }
            }
            """;
        var provider = new TencentSnapshotProvider(new HttpClient(new FixtureHandler(json)));

        var result = await provider.GetBarsAsync(
            TestData.Stock,
            Timeframe.Day,
            30,
            CancellationToken.None);
        var bar = Assert.Single(result.Data);

        Assert.Equal(Timeframe.Day, bar.Timeframe);
        Assert.Equal(1_110_800, bar.Volume);
        Assert.Equal(1253m, bar.Close);
        Assert.True(bar.IsFinal);
    }

    private sealed class FixtureHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }
}
