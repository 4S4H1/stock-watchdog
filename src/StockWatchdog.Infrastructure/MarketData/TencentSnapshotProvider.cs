using System.Globalization;
using System.Text;
using System.Text.Json;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Infrastructure.MarketData;

public sealed class TencentSnapshotProvider : IMarketDataProvider
{
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
    private readonly HttpClient _httpClient;

    static TencentSnapshotProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public TencentSnapshotProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) StockWatchdog/1.0");
        _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://gu.qq.com/");
    }

    public string Name => "TencentPublic";

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

        var symbols = string.Join(',', instruments.Select(x => x.TencentSymbol));
        var uri = $"https://qt.gtimg.cn/q={Uri.EscapeDataString(symbols)}";
        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var content = Encoding.GetEncoding("GB18030").GetString(bytes);
            var lookup = instruments.ToDictionary(x => x.TencentSymbol, StringComparer.OrdinalIgnoreCase);
            var quotes = new List<QuoteSnapshot>();

            foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = line.IndexOf('=');
                if (equals < 3)
                {
                    continue;
                }

                var symbol = line[2..equals];
                if (!lookup.TryGetValue(symbol, out var instrument))
                {
                    continue;
                }

                var payload = line[(equals + 1)..].Trim().Trim('"', ';');
                var fields = payload.Split('~');
                if (fields.Length < 35
                    || !TryDecimal(fields[3], out var price)
                    || !TryDecimal(fields[4], out var previousClose)
                    || price <= 0
                    || previousClose <= 0)
                {
                    continue;
                }

                var sourceTime = ParseTime(fields.ElementAtOrDefault(30), receivedAt);
                var flags = sourceTime == receivedAt
                    ? DataQualityFlags.MissingTimestamp | DataQualityFlags.ParseFallback
                    : DataQualityFlags.ParseFallback;
                _ = long.TryParse(
                    fields.ElementAtOrDefault(6),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var volumeHands);
                var change = TryDecimal(fields.ElementAtOrDefault(31), out var parsedChange)
                    ? parsedChange
                    : price - previousClose;
                var changePercent = TryDecimal(fields.ElementAtOrDefault(32), out var parsedPercent)
                    ? parsedPercent
                    : change / previousClose * 100;

                quotes.Add(new QuoteSnapshot(
                    instrument,
                    fields[1],
                    price,
                    previousClose,
                    change,
                    changePercent,
                    volumeHands * 100,
                    0,
                    sourceTime,
                    receivedAt,
                    Name,
                    MarketDataQuality.Delayed,
                    flags));
            }

            return quotes.Count == 0
                ? ProviderResult<IReadOnlyList<QuoteSnapshot>>.Failure(
                    [],
                    Name,
                    receivedAt,
                    "未解析到备用行情")
                : new ProviderResult<IReadOnlyList<QuoteSnapshot>>(
                    true,
                    quotes,
                    MarketDataQuality.Delayed,
                    Name,
                    receivedAt);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or TaskCanceledException
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
        var receivedAt = DateTimeOffset.Now;
        var uri = timeframe switch
        {
            Timeframe.Minute60 =>
                $"https://ifzq.gtimg.cn/appstock/app/kline/mkline?param={instrument.TencentSymbol},m60,,{Math.Clamp(limit, 30, 500)}",
            Timeframe.Day =>
                $"https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param={instrument.TencentSymbol},day,,,{Math.Clamp(limit, 30, 500)},qfq",
            _ => null
        };
        if (uri is null)
        {
            return ProviderResult<IReadOnlyList<Bar>>.Failure(
                [],
                Name,
                receivedAt,
                "备用源仅提供 60 分钟线和日线");
        }

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty(instrument.TencentSymbol, out var instrumentData))
            {
                return ProviderResult<IReadOnlyList<Bar>>.Failure(
                    [],
                    Name,
                    receivedAt,
                    "备用 K 线响应缺少标的数据");
            }

            JsonElement rows;
            if (timeframe == Timeframe.Minute60)
            {
                if (!instrumentData.TryGetProperty("m60", out rows))
                {
                    return ProviderResult<IReadOnlyList<Bar>>.Failure(
                        [],
                        Name,
                        receivedAt,
                        "备用响应缺少 m60");
                }
            }
            else if (!instrumentData.TryGetProperty("qfqday", out rows)
                     && !instrumentData.TryGetProperty("day", out rows))
            {
                return ProviderResult<IReadOnlyList<Bar>>.Failure(
                    [],
                    Name,
                    receivedAt,
                    "备用响应缺少 day");
            }

            var bars = rows.EnumerateArray()
                .Select(row => timeframe == Timeframe.Minute60
                    ? ParseHourlyBar(instrument, row, receivedAt)
                    : ParseDailyBar(instrument, row, receivedAt))
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
                    "备用源未解析到有效 K 线")
                : new ProviderResult<IReadOnlyList<Bar>>(
                    true,
                    bars,
                    MarketDataQuality.Delayed,
                    Name,
                    receivedAt);
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

    private static DateTimeOffset ParseTime(string? value, DateTimeOffset fallback)
    {
        if (DateTime.TryParseExact(
            value,
            "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed))
        {
            return new DateTimeOffset(parsed, ChinaOffset);
        }

        return fallback;
    }

    private static bool TryDecimal(string? value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static Bar? ParseHourlyBar(
        InstrumentId instrument,
        JsonElement row,
        DateTimeOffset receivedAt)
    {
        if (row.ValueKind != JsonValueKind.Array
            || row.GetArrayLength() < 6
            || !DateTime.TryParseExact(
                row[0].GetString(),
                "yyyyMMddHHmm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var endDateTime)
            || !TryDecimal(row[1].GetString(), out var open)
            || !TryDecimal(row[2].GetString(), out var close)
            || !TryDecimal(row[3].GetString(), out var high)
            || !TryDecimal(row[4].GetString(), out var low)
            || !TryDecimal(row[5].GetString(), out var volumeHands))
        {
            return null;
        }

        var end = new DateTimeOffset(endDateTime, ChinaOffset);
        var completedMinute = new DateTimeOffset(
            receivedAt.Year,
            receivedAt.Month,
            receivedAt.Day,
            receivedAt.Hour,
            receivedAt.Minute,
            0,
            receivedAt.Offset);
        return new Bar(
            instrument,
            Timeframe.Minute60,
            end.AddHours(-1),
            end,
            open,
            high,
            low,
            close,
            ToShares(volumeHands),
            0,
            end <= completedMinute,
            "TencentPublicKline",
            DataQualityFlags.ParseFallback
            | (volumeHands == 0 ? DataQualityFlags.ZeroVolume : DataQualityFlags.None));
    }

    private static Bar? ParseDailyBar(
        InstrumentId instrument,
        JsonElement row,
        DateTimeOffset receivedAt)
    {
        if (row.ValueKind != JsonValueKind.Array
            || row.GetArrayLength() < 6
            || !DateTime.TryParseExact(
                row[0].GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateTime)
            || !TryDecimal(row[1].GetString(), out var open)
            || !TryDecimal(row[2].GetString(), out var close)
            || !TryDecimal(row[3].GetString(), out var high)
            || !TryDecimal(row[4].GetString(), out var low)
            || !TryDecimal(row[5].GetString(), out var volumeHands))
        {
            return null;
        }

        var start = new DateTimeOffset(dateTime, ChinaOffset);
        var chinaReceivedAt = receivedAt.ToOffset(ChinaOffset);
        var isFinal = DateOnly.FromDateTime(dateTime) < DateOnly.FromDateTime(chinaReceivedAt.DateTime)
            || chinaReceivedAt.TimeOfDay >= TimeSpan.FromHours(15);
        return new Bar(
            instrument,
            Timeframe.Day,
            start,
            start.AddDays(1),
            open,
            high,
            low,
            close,
            ToShares(volumeHands),
            0,
            isFinal,
            "TencentPublicKline",
            DataQualityFlags.ParseFallback
            | (volumeHands == 0 ? DataQualityFlags.ZeroVolume : DataQualityFlags.None));
    }

    private static long ToShares(decimal volumeHands) =>
        decimal.ToInt64(decimal.Truncate(volumeHands * 100m));
}
