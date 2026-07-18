using StockWatchdog.Domain.Market;

namespace StockWatchdog.Domain.Analysis;

public enum ChartTradeSide
{
    Buy,
    Sell
}

public enum TrendFrameDirection
{
    Flat,
    RisingProfit,
    FallingLoss
}

public sealed record ChartTradeMarker(
    Guid Id,
    InstrumentId Instrument,
    Timeframe Timeframe,
    ChartTradeSide Side,
    DateTimeOffset Time,
    decimal Price,
    DateTimeOffset CreatedAt);

public sealed record ChartTrendFrame(
    ChartTradeMarker Marker,
    DateTimeOffset EndTime,
    decimal EndPrice,
    decimal Change,
    decimal ChangePercent,
    TrendFrameDirection Direction);
