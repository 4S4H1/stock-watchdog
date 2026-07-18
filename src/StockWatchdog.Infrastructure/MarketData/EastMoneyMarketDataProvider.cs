using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Application.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Infrastructure.MarketData;

public sealed class EastMoneyMarketDataProvider : IMarketDataProvider
{
    private const string Token = "bd1d9ddb04089700cf9c27f6f7426281";
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
    private readonly HttpClient _httpClient;

    public EastMoneyMarketDataProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) StockWatchdog/1.0");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://quote.eastmoney.com/");
    }

    public string Name => "EastMoneyPublic";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Quotes | ProviderCapabilities.MinuteBars | ProviderCapabilities.DailyBars;

    public async Task<ProviderResult<IReadOnlyList<QuoteSnapshot>>> GetQuotesAsync(
        IReadOnlyCollection<InstrumentId> instruments,
        CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.Now;
        if (instruments.Count == 0)
        {
            return ProviderResult<IReadOnlyList<QuoteSnapshot>>.Success([], Name, receivedAt);
        }

        var secids = string.Join(',', instruments.Select(x => x.EastMoneySecId));
        var fields = "f2,f3,f4,f5,f6,f12,f13,f14,f18,f124";
        var uri =
            $"https://push2.eastmoney.com/api/qt/ulist.np/get?fltt=2&invt=2"
            + $"&ut={Token}&fields={fields}&secids={Uri.EscapeDataString(secids)}";

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind == JsonValueKind.Null
                || !data.TryGetProperty("diff", out var diff)
                || diff.ValueKind != JsonValueKind.Array)
            {
                return ProviderResult<IReadOnlyList<QuoteSnapshot>>.Failure(
                    [],
                    Name,
                    receivedAt,
                    "行情响应缺少 data.diff");
            }

            var lookup = instruments.ToDictionary(x => (x.Exchange, x.Code));
            var quotes = new List<QuoteSnapshot>();
            foreach (var item in diff.EnumerateArray())
            {
                var code = ReadString(item, "f12");
                var market = ReadInt(item, "f13");
                var exchange = market == 1 ? Exchange.Shanghai : Exchange.Shenzhen;
                if (code is null || !lookup.TryGetValue((exchange, code), out var instrument))
                {
                    continue;
                }

                var price = ReadDecimal(item, "f2");
                var previousClose = ReadDecimal(item, "f18");
                if (price <= 0 || previousClose <= 0)
                {
                    continue;
                }

                var unixTime = ReadLong(item, "f124");
                var flags = DataQualityFlags.None;
                var sourceTime = unixTime > 1_500_000_000
                    ? DateTimeOffset.FromUnixTimeSeconds(unixTime).ToOffset(ChinaOffset)
                    : receivedAt;
                if (unixTime <= 1_500_000_000)
                {
                    flags |= DataQualityFlags.MissingTimestamp;
                }

                var volumeHands = ReadLong(item, "f5");
                quotes.Add(new QuoteSnapshot(
                    instrument,
                    ReadString(item, "f14") ?? code,
                    price,
                    previousClose,
                    ReadDecimal(item, "f4"),
                    ReadDecimal(item, "f3"),
                    volumeHands * 100,
                    ReadDecimal(item, "f6"),
                    sourceTime,
                    receivedAt,
                    Name,
                    MarketDataQuality.Healthy,
                    flags));
            }

            return quotes.Count == 0
                ? ProviderResult<IReadOnlyList<QuoteSnapshot>>.Failure(
                    [],
                    Name,
                    receivedAt,
                    "未解析到有效行情")
                : ProviderResult<IReadOnlyList<QuoteSnapshot>>.Success(quotes, Name, receivedAt);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or InvalidOperationException)
        {
            return ProviderResult<IReadOnlyList<QuoteSnapshot>>.Failure(
                [],
                Name,
                receivedAt,
                exception.Message);
        }
    }

    public async Task<ProviderResult<IReadOnlyList<Bar>>> GetBarsAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        int limit,
        CancellationToken cancellationToken)
    {
        if (timeframe == Timeframe.Day)
        {
            return await GetDailyBarsAsync(instrument, limit, cancellationToken).ConfigureAwait(false);
        }

        if (timeframe == Timeframe.Minute60)
        {
            return await GetHourlyBarsAsync(instrument, limit, cancellationToken).ConfigureAwait(false);
        }

        var minuteResult = await GetMinuteBarsAsync(
                instrument,
                Math.Clamp(limit * Math.Max(1, (int)timeframe), 100, 1_500),
                cancellationToken)
            .ConfigureAwait(false);
        if (!minuteResult.IsSuccess || timeframe == Timeframe.Minute1)
        {
            return minuteResult;
        }

        var aggregated = BarAggregator.Aggregate(minuteResult.Data, timeframe)
            .TakeLast(limit)
            .ToArray();
        return ProviderResult<IReadOnlyList<Bar>>.Success(aggregated, Name, minuteResult.ReceivedAt);
    }

    private async Task<ProviderResult<IReadOnlyList<Bar>>> GetMinuteBarsAsync(
        InstrumentId instrument,
        int limit,
        CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.Now;
        var uri =
            "https://push2his.eastmoney.com/api/qt/stock/trends2/get"
            + $"?secid={instrument.EastMoneySecId}&ndays=5&iscr=0&ut={Token}"
            + "&fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13"
            + "&fields2=f51,f52,f53,f54,f55,f56,f57,f58";

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            using (document)
            {
                if (document is null
                    || !document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind == JsonValueKind.Null
                    || !data.TryGetProperty("trends", out var trends)
                    || trends.ValueKind != JsonValueKind.Array)
                {
                    return ProviderResult<IReadOnlyList<Bar>>.Failure(
                        [],
                        Name,
                        receivedAt,
                        "分钟线响应缺少 data.trends");
                }

                var parsedBars = trends.EnumerateArray()
                    .Select(x => ParseMinuteBar(instrument, x.GetString(), receivedAt))
                    .Where(x => x is not null)
                    .Cast<Bar>()
                    .OrderBy(x => x.StartTime)
                    .ToArray();
                var bars = NormalizeMinuteBars(parsedBars)
                    .TakeLast(limit)
                    .ToArray();
                return bars.Length == 0
                    ? ProviderResult<IReadOnlyList<Bar>>.Failure(
                        [],
                        Name,
                        receivedAt,
                        "未解析到有效分钟线")
                    : ProviderResult<IReadOnlyList<Bar>>.Success(bars, Name, receivedAt);
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or InvalidOperationException)
        {
            return ProviderResult<IReadOnlyList<Bar>>.Failure(
                [],
                Name,
                receivedAt,
                exception.Message);
        }
    }

    private async Task<ProviderResult<IReadOnlyList<Bar>>> GetDailyBarsAsync(
        InstrumentId instrument,
        int limit,
        CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.Now;
        var uri =
            "https://push2his.eastmoney.com/api/qt/stock/kline/get"
            + $"?secid={instrument.EastMoneySecId}&klt=101&fqt=1&lmt={Math.Clamp(limit, 30, 500)}"
            + $"&ut={Token}&fields1=f1,f2,f3,f4,f5,f6"
            + "&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61";

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            using (document)
            {
                if (document is null
                    || !document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind == JsonValueKind.Null
                    || !data.TryGetProperty("klines", out var klines)
                    || klines.ValueKind != JsonValueKind.Array)
                {
                    return ProviderResult<IReadOnlyList<Bar>>.Failure(
                        [],
                        Name,
                        receivedAt,
                        "日线响应缺少 data.klines");
                }

                var bars = klines.EnumerateArray()
                    .Select(x => ParseDailyBar(instrument, x.GetString(), receivedAt))
                    .Where(x => x is not null)
                    .Cast<Bar>()
                    .Where(BarIntegrity.HasValidPrices)
                    .OrderBy(x => x.StartTime)
                    .TakeLast(limit)
                    .ToArray();
                return bars.Length == 0
                    ? ProviderResult<IReadOnlyList<Bar>>.Failure(
                        [],
                        Name,
                        receivedAt,
                        "未解析到有效日线")
                    : ProviderResult<IReadOnlyList<Bar>>.Success(bars, Name, receivedAt);
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or InvalidOperationException)
        {
            return ProviderResult<IReadOnlyList<Bar>>.Failure(
                [],
                Name,
                receivedAt,
                exception.Message);
        }
    }

    private async Task<ProviderResult<IReadOnlyList<Bar>>> GetHourlyBarsAsync(
        InstrumentId instrument,
        int limit,
        CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.Now;
        var uri =
            "https://push2his.eastmoney.com/api/qt/stock/kline/get"
            + $"?secid={instrument.EastMoneySecId}&klt=60&fqt=1&lmt={Math.Clamp(limit, 30, 500)}"
            + $"&ut={Token}&fields1=f1,f2,f3,f4,f5,f6"
            + "&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61";

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            using (document)
            {
                if (document is null
                    || !document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind == JsonValueKind.Null
                    || !data.TryGetProperty("klines", out var klines)
                    || klines.ValueKind != JsonValueKind.Array)
                {
                    return ProviderResult<IReadOnlyList<Bar>>.Failure(
                        [],
                        Name,
                        receivedAt,
                        "60 分钟线响应缺少 data.klines");
                }

                var bars = klines.EnumerateArray()
                    .Select(x => ParseHourlyBar(instrument, x.GetString(), receivedAt))
                    .Where(x => x is not null)
                    .Cast<Bar>()
                    .Where(BarIntegrity.HasValidPrices)
                    .OrderBy(x => x.StartTime)
                    .TakeLast(limit)
                    .ToArray();
                return bars.Length == 0
                    ? ProviderResult<IReadOnlyList<Bar>>.Failure(
                        [],
                        Name,
                        receivedAt,
                        "未解析到有效 60 分钟线")
                    : ProviderResult<IReadOnlyList<Bar>>.Success(bars, Name, receivedAt);
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException
                or JsonException
                or InvalidOperationException)
        {
            return ProviderResult<IReadOnlyList<Bar>>.Failure(
                [],
                Name,
                receivedAt,
                exception.Message);
        }
    }

    private static Bar? ParseMinuteBar(
        InstrumentId instrument,
        string? value,
        DateTimeOffset receivedAt)
    {
        var parts = value?.Split(',');
        if (parts is null
            || parts.Length < 8
            || !DateTime.TryParseExact(
                parts[0],
                "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateTime)
            || !TryDecimal(parts[1], out var open)
            || !TryDecimal(parts[2], out var close)
            || !TryDecimal(parts[3], out var high)
            || !TryDecimal(parts[4], out var low)
            || !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var volumeHands)
            || !TryDecimal(parts[6], out var turnover))
        {
            return null;
        }

        var start = new DateTimeOffset(dateTime, ChinaOffset);
        var end = start + TimeSpan.FromMinutes(1);
        var completedMinute = new DateTimeOffset(
            receivedAt.Year,
            receivedAt.Month,
            receivedAt.Day,
            receivedAt.Hour,
            receivedAt.Minute,
            0,
            receivedAt.Offset);
        var flags = volumeHands == 0 ? DataQualityFlags.ZeroVolume : DataQualityFlags.None;
        return new Bar(
            instrument,
            Timeframe.Minute1,
            start,
            end,
            open,
            high,
            low,
            close,
            volumeHands * 100,
            turnover,
            end <= completedMinute,
            "EastMoneyPublic",
            flags);
    }

    private static IReadOnlyList<Bar> NormalizeMinuteBars(IReadOnlyList<Bar> source)
    {
        var normalized = new List<Bar>(source.Count);
        Bar? previous = null;
        foreach (var bar in source.OrderBy(x => x.StartTime))
        {
            var candidate = bar;
            if (!BarIntegrity.HasValidPrices(candidate)
                && candidate.Open <= 0
                && candidate.Close > 0
                && candidate.Low <= candidate.Close
                && candidate.High >= candidate.Close)
            {
                var previousClose = previous is not null
                                    && DateOnly.FromDateTime(previous.StartTime.LocalDateTime)
                                    == DateOnly.FromDateTime(candidate.StartTime.LocalDateTime)
                                    && previous.Close >= candidate.Low
                                    && previous.Close <= candidate.High
                    ? previous.Close
                    : candidate.Close;
                candidate = candidate with
                {
                    Open = previousClose,
                    QualityFlags = candidate.QualityFlags
                                   | DataQualityFlags.Corrected
                                   | DataQualityFlags.ParseFallback
                };
            }

            if (!BarIntegrity.HasValidPrices(candidate))
            {
                continue;
            }

            normalized.Add(candidate);
            previous = candidate;
        }

        return normalized;
    }

    private static Bar? ParseDailyBar(
        InstrumentId instrument,
        string? value,
        DateTimeOffset receivedAt)
    {
        var parts = value?.Split(',');
        if (parts is null
            || parts.Length < 7
            || !DateTime.TryParseExact(
                parts[0],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateTime)
            || !TryDecimal(parts[1], out var open)
            || !TryDecimal(parts[2], out var close)
            || !TryDecimal(parts[3], out var high)
            || !TryDecimal(parts[4], out var low)
            || !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var volumeHands)
            || !TryDecimal(parts[6], out var turnover))
        {
            return null;
        }

        var start = new DateTimeOffset(dateTime, ChinaOffset);
        var end = start.AddDays(1);
        var chinaReceivedAt = receivedAt.ToOffset(ChinaOffset);
        var isFinal = DateOnly.FromDateTime(dateTime) < DateOnly.FromDateTime(chinaReceivedAt.DateTime)
            || chinaReceivedAt.TimeOfDay >= TimeSpan.FromHours(15);
        var flags = volumeHands == 0 ? DataQualityFlags.ZeroVolume : DataQualityFlags.None;
        return new Bar(
            instrument,
            Timeframe.Day,
            start,
            end,
            open,
            high,
            low,
            close,
            volumeHands * 100,
            turnover,
            isFinal,
            "EastMoneyPublic",
            flags);
    }

    private static Bar? ParseHourlyBar(
        InstrumentId instrument,
        string? value,
        DateTimeOffset receivedAt)
    {
        var parts = value?.Split(',');
        if (parts is null
            || parts.Length < 7
            || !DateTime.TryParseExact(
                parts[0],
                "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var endDateTime)
            || !TryDecimal(parts[1], out var open)
            || !TryDecimal(parts[2], out var close)
            || !TryDecimal(parts[3], out var high)
            || !TryDecimal(parts[4], out var low)
            || !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var volumeHands)
            || !TryDecimal(parts[6], out var turnover))
        {
            return null;
        }

        var end = new DateTimeOffset(endDateTime, ChinaOffset);
        var start = end.AddHours(-1);
        var completedHour = new DateTimeOffset(
            receivedAt.Year,
            receivedAt.Month,
            receivedAt.Day,
            receivedAt.Hour,
            receivedAt.Minute,
            0,
            receivedAt.Offset);
        var flags = volumeHands == 0 ? DataQualityFlags.ZeroVolume : DataQualityFlags.None;
        return new Bar(
            instrument,
            Timeframe.Minute60,
            start,
            end,
            open,
            high,
            low,
            close,
            volumeHands * 100,
            turnover,
            end <= completedHour,
            "EastMoneyPublic",
            flags);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static decimal ReadDecimal(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && TryDecimal(value.ToString(), out var result)
            ? result
            : 0;

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

    private static bool TryDecimal(string? value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
