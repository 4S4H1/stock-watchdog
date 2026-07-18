using System.Globalization;

namespace StockWatchdog.Domain.Market;

public enum Exchange
{
    Shanghai,
    Shenzhen
}

public enum AssetType
{
    Stock,
    Etf
}

public enum Timeframe
{
    Minute1 = 1,
    Minute5 = 5,
    Minute15 = 15,
    Minute60 = 60,
    Day = 1440
}

public enum MarketDataQuality
{
    Healthy,
    Delayed,
    Stale,
    Divergent,
    Unavailable,
    WarmingUp
}

[Flags]
public enum DataQualityFlags
{
    None = 0,
    MissingTimestamp = 1,
    OutOfOrder = 2,
    Duplicate = 4,
    Corrected = 8,
    ZeroVolume = 16,
    SourceMismatch = 32,
    Suspended = 64,
    ParseFallback = 128,
    SecondaryUnverified = 256
}

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Quotes = 1,
    MinuteBars = 2,
    DailyBars = 4
}

public readonly record struct InstrumentId(Exchange Exchange, string Code, AssetType AssetType)
{
    public string DisplayCode => Code;

    public string EastMoneySecId => $"{(Exchange == Exchange.Shanghai ? 1 : 0)}.{Code}";

    public string TencentSymbol => $"{(Exchange == Exchange.Shanghai ? "sh" : "sz")}{Code}";

    public override string ToString() => $"{(Exchange == Exchange.Shanghai ? "SH" : "SZ")}.{Code}";

    public static bool TryParse(string? input, out InstrumentId instrument)
    {
        instrument = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = input.Trim().ToUpperInvariant()
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        Exchange exchange;
        string code;
        if (normalized.StartsWith("SH", StringComparison.Ordinal))
        {
            exchange = Exchange.Shanghai;
            code = normalized[2..];
        }
        else if (normalized.StartsWith("SZ", StringComparison.Ordinal))
        {
            exchange = Exchange.Shenzhen;
            code = normalized[2..];
        }
        else
        {
            code = normalized;
            exchange = code.StartsWith('5') || code.StartsWith('6')
                ? Exchange.Shanghai
                : Exchange.Shenzhen;
        }

        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            return false;
        }

        var assetType = code.StartsWith('5')
            || code.StartsWith("15", StringComparison.Ordinal)
            || code.StartsWith("16", StringComparison.Ordinal)
            || code.StartsWith("18", StringComparison.Ordinal)
            ? AssetType.Etf
            : AssetType.Stock;

        instrument = new InstrumentId(exchange, code, assetType);
        return true;
    }
}

public sealed record QuoteSnapshot(
    InstrumentId Instrument,
    string Name,
    decimal Price,
    decimal PreviousClose,
    decimal Change,
    decimal ChangePercent,
    long Volume,
    decimal Turnover,
    DateTimeOffset SourceTime,
    DateTimeOffset ReceivedTime,
    string Source,
    MarketDataQuality Quality,
    DataQualityFlags QualityFlags = DataQualityFlags.None)
{
    public TimeSpan Age(DateTimeOffset now) => now - SourceTime;

    public QuoteSnapshot WithFreshness(DateTimeOffset now, TimeSpan staleAfter)
    {
        var age = Age(now);
        var quality = age > staleAfter
            ? MarketDataQuality.Stale
            : age > TimeSpan.FromTicks(staleAfter.Ticks / 2)
                ? MarketDataQuality.Delayed
                : Quality;
        return this with { Quality = quality };
    }
}

public sealed record Bar(
    InstrumentId Instrument,
    Timeframe Timeframe,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    decimal Turnover,
    bool IsFinal,
    string Source,
    DataQualityFlags QualityFlags = DataQualityFlags.None)
{
    public string Id => string.Create(
        CultureInfo.InvariantCulture,
        $"{Instrument}:{(int)Timeframe}:{StartTime:O}");
}

public sealed record WatchItem(
    InstrumentId Instrument,
    string Name,
    int SortOrder,
    bool AnalysisEnabled = true,
    bool IsMuted = false,
    string? CustomName = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(CustomName)
        ? Name
        : CustomName.Trim();
}
