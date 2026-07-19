using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Application.Analysis;

public sealed class TSignalEngine
{
    public const string Version = "1.0.0";

    public TSignalSnapshot Analyze(
        QuoteSnapshot quote,
        IReadOnlyDictionary<Timeframe, AnalysisSnapshot> snapshots,
        QuoteFlowMetrics flow,
        DateTimeOffset now,
        int candidateThreshold = 75)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        candidateThreshold = Math.Clamp(candidateThreshold, 60, 95);

        if (quote.Quality != MarketDataQuality.Healthy)
        {
            return TSignalSnapshot.Unavailable(
                quote.Instrument,
                now,
                quote.Quality,
                "行情质量异常，评分暂停");
        }

        if (!snapshots.TryGetValue(Timeframe.Minute1, out var minute1)
            || minute1.DataQuality != MarketDataQuality.Healthy
            || minute1.Indicators.Count < 2
            || minute1.Bars.Count < 26)
        {
            return TSignalSnapshot.Unavailable(
                quote.Instrument,
                now,
                minute1?.DataQuality ?? MarketDataQuality.WarmingUp,
                "分钟数据预热中");
        }

        var latest = minute1.Indicators[^1];
        var previous = minute1.Indicators[^2];
        var latestBar = minute1.Bars[^1];
        if (latest.Atr14 is not { } atr
            || atr <= 0
            || latest.Rsi14 is not { } rsi
            || quote.Price <= 0)
        {
            return TSignalSnapshot.Unavailable(
                quote.Instrument,
                now,
                MarketDataQuality.WarmingUp,
                "量价指标预热中");
        }

        var evidence = new List<TScoreEvidence>();
        var buy = 5;
        var sell = 5;

        void Add(TScoreSide side, string factor, int points, string description)
        {
            if (points <= 0)
            {
                return;
            }

            if (side == TScoreSide.Buy)
            {
                buy += points;
            }
            else
            {
                sell += points;
            }

            evidence.Add(new TScoreEvidence(side, factor, points, description));
        }

        void AddBoth(string factor, int points, string description)
        {
            Add(TScoreSide.Buy, factor, points, description);
            Add(TScoreSide.Sell, factor, points, description);
        }

        var atrPercent = atr / quote.Price * 100m;
        var hasTradingRange = atrPercent >= 0.025m;
        if (hasTradingRange)
        {
            AddBoth("波动空间", 7, $"分钟 ATR 为现价的 {atrPercent:0.000}%");
        }

        decimal? vwapDeviation = null;
        if (latest.Vwap is { } vwap && vwap > 0)
        {
            vwapDeviation = (quote.Price - vwap) / atr;
            if (vwapDeviation <= -1.2m)
            {
                Add(TScoreSide.Buy, "价格位置", 25, "价格显著低于 VWAP");
            }
            else if (vwapDeviation <= -0.6m)
            {
                Add(TScoreSide.Buy, "价格位置", 15, "价格位于 VWAP 下方");
            }
            else if (vwapDeviation >= 1.2m)
            {
                Add(TScoreSide.Sell, "价格位置", 25, "价格显著高于 VWAP");
            }
            else if (vwapDeviation >= 0.6m)
            {
                Add(TScoreSide.Sell, "价格位置", 15, "价格位于 VWAP 上方");
            }
        }

        if (rsi <= 30m)
        {
            Add(TScoreSide.Buy, "动量", 20, $"RSI {rsi:0.0} 进入超卖区");
        }
        else if (rsi <= 40m)
        {
            Add(TScoreSide.Buy, "动量", 11, $"RSI {rsi:0.0} 偏低");
        }
        else if (rsi >= 70m)
        {
            Add(TScoreSide.Sell, "动量", 20, $"RSI {rsi:0.0} 进入超买区");
        }
        else if (rsi >= 60m)
        {
            Add(TScoreSide.Sell, "动量", 11, $"RSI {rsi:0.0} 偏高");
        }

        if (previous.Rsi14 is { } previousRsi)
        {
            if (previousRsi <= 35m && rsi >= previousRsi + 1m)
            {
                Add(TScoreSide.Buy, "动量拐点", 6, "RSI 从低位回升");
            }
            else if (previousRsi >= 65m && rsi <= previousRsi - 1m)
            {
                Add(TScoreSide.Sell, "动量拐点", 6, "RSI 从高位回落");
            }
        }

        AddMacdEvidence(previous, latest, Add);

        if (latest.BollingerLower is { } lower
            && latest.BollingerUpper is { } upper
            && upper > lower)
        {
            var bandTolerance = (upper - lower) * 0.08m;
            if (quote.Price <= lower + bandTolerance)
            {
                Add(TScoreSide.Buy, "布林位置", 15, "价格接近布林下轨");
            }

            if (quote.Price >= upper - bandTolerance)
            {
                Add(TScoreSide.Sell, "布林位置", 15, "价格接近布林上轨");
            }
        }

        if (latest.VolumeRatio is { } volumeRatio)
        {
            if (volumeRatio >= 1.8m)
            {
                if (latestBar.Close >= latestBar.Open)
                {
                    Add(TScoreSide.Buy, "成交量", 12, $"上涨放量 {volumeRatio:0.00} 倍");
                }
                else
                {
                    Add(TScoreSide.Sell, "成交量", 12, $"下跌放量 {volumeRatio:0.00} 倍");
                }
            }
            else if (volumeRatio <= 0.8m && vwapDeviation is { } deviation)
            {
                if (deviation < -0.4m && latestBar.Close >= latestBar.Open)
                {
                    Add(TScoreSide.Buy, "成交量", 6, $"低位缩量企稳 {volumeRatio:0.00} 倍");
                }
                else if (deviation > 0.4m && latestBar.Close <= latestBar.Open)
                {
                    Add(TScoreSide.Sell, "成交量", 6, $"高位缩量转弱 {volumeRatio:0.00} 倍");
                }
            }
        }

        if (flow.IsReliable)
        {
            if (flow.VolumeAcceleration >= 1.5m && flow.PriceChangePercent >= 0.03m)
            {
                Add(
                    TScoreSide.Buy,
                    "实时量速",
                    8,
                    $"短时量速提升至 {flow.VolumeAcceleration:0.0} 倍");
            }
            else if (flow.VolumeAcceleration >= 1.5m && flow.PriceChangePercent <= -0.03m)
            {
                Add(
                    TScoreSide.Sell,
                    "实时量速",
                    8,
                    $"下跌量速提升至 {flow.VolumeAcceleration:0.0} 倍");
            }
        }

        AddPatternEvidence(minute1, 8, Add);
        if (snapshots.TryGetValue(Timeframe.Minute5, out var minute5)
            && minute5.DataQuality == MarketDataQuality.Healthy)
        {
            AddPatternEvidence(minute5, 10, Add);
        }

        AddRegimeEvidence(snapshots, Add);

        if (IsRegime(snapshots, Timeframe.Minute15, MarketRegime.TrendingDown)
            && IsRegime(snapshots, Timeframe.Minute60, MarketRegime.TrendingDown))
        {
            buy -= 10;
        }

        if (IsRegime(snapshots, Timeframe.Minute15, MarketRegime.TrendingUp)
            && IsRegime(snapshots, Timeframe.Minute60, MarketRegime.TrendingUp))
        {
            sell -= 10;
        }

        buy = Math.Clamp(buy, 0, 100);
        sell = Math.Clamp(sell, 0, 100);
        if (!hasTradingRange)
        {
            buy = Math.Min(buy, candidateThreshold - 1);
            sell = Math.Min(sell, candidateThreshold - 1);
        }

        var watchThreshold = Math.Max(50, candidateThreshold - 15);
        var state = ResolveState(buy, sell, watchThreshold, candidateThreshold);
        var summary = BuildSummary(
            state,
            buy,
            sell,
            hasTradingRange,
            flow.IsReliable,
            evidence);
        return new TSignalSnapshot(
            quote.Instrument,
            buy,
            sell,
            state,
            summary,
            now,
            now + TimeSpan.FromMinutes(2),
            MarketDataQuality.Healthy,
            evidence
                .OrderByDescending(item => item.Points)
                .ThenBy(item => item.Factor)
                .ToArray());
    }

    private static void AddMacdEvidence(
        IndicatorPoint previous,
        IndicatorPoint latest,
        Action<TScoreSide, string, int, string> add)
    {
        if (latest.Macd is not { } macd
            || latest.MacdSignal is not { } signal
            || previous.Macd is not { } previousMacd
            || previous.MacdSignal is not { } previousSignal)
        {
            return;
        }

        if (macd > signal && previousMacd <= previousSignal)
        {
            add(TScoreSide.Buy, "MACD", 10, "MACD 向上交叉");
        }
        else if (macd < signal && previousMacd >= previousSignal)
        {
            add(TScoreSide.Sell, "MACD", 10, "MACD 向下交叉");
        }
        else if (macd > signal)
        {
            add(TScoreSide.Buy, "MACD", 4, "MACD 位于信号线上方");
        }
        else if (macd < signal)
        {
            add(TScoreSide.Sell, "MACD", 4, "MACD 位于信号线下方");
        }
    }

    private static void AddPatternEvidence(
        AnalysisSnapshot snapshot,
        int maximumPoints,
        Action<TScoreSide, string, int, string> add)
    {
        var bullish = snapshot.Findings
            .Where(item => item.Direction == PatternDirection.Bullish)
            .MaxBy(item => item.Score);
        if (bullish is not null)
        {
            var points = Math.Clamp((bullish.Score - 40) / 4, 3, maximumPoints);
            add(TScoreSide.Buy, "技术形态", points, bullish.DisplayName);
        }

        var bearish = snapshot.Findings
            .Where(item => item.Direction == PatternDirection.Bearish)
            .MaxBy(item => item.Score);
        if (bearish is not null)
        {
            var points = Math.Clamp((bearish.Score - 40) / 4, 3, maximumPoints);
            add(TScoreSide.Sell, "技术形态", points, bearish.DisplayName);
        }
    }

    private static void AddRegimeEvidence(
        IReadOnlyDictionary<Timeframe, AnalysisSnapshot> snapshots,
        Action<TScoreSide, string, int, string> add)
    {
        foreach (var (timeframe, points) in new[]
                 {
                     (Timeframe.Minute5, 8),
                     (Timeframe.Minute15, 7),
                     (Timeframe.Minute60, 5),
                     (Timeframe.Day, 5)
                 })
        {
            if (!snapshots.TryGetValue(timeframe, out var snapshot)
                || snapshot.DataQuality != MarketDataQuality.Healthy)
            {
                continue;
            }

            var label = timeframe == Timeframe.Day ? "日线" : $"{(int)timeframe}分钟";
            if (snapshot.Regime == MarketRegime.TrendingUp)
            {
                add(TScoreSide.Buy, "多周期趋势", points, $"{label}趋势偏强");
            }
            else if (snapshot.Regime == MarketRegime.TrendingDown)
            {
                add(TScoreSide.Sell, "多周期趋势", points, $"{label}趋势偏弱");
            }
        }
    }

    private static bool IsRegime(
        IReadOnlyDictionary<Timeframe, AnalysisSnapshot> snapshots,
        Timeframe timeframe,
        MarketRegime regime) =>
        snapshots.TryGetValue(timeframe, out var snapshot)
        && snapshot.DataQuality == MarketDataQuality.Healthy
        && snapshot.Regime == regime;

    private static TSignalState ResolveState(
        int buy,
        int sell,
        int watchThreshold,
        int candidateThreshold)
    {
        if (buy >= candidateThreshold && sell >= candidateThreshold)
        {
            return TSignalState.Volatile;
        }

        if (buy >= candidateThreshold && buy >= sell + 8)
        {
            return TSignalState.BuyCandidate;
        }

        if (sell >= candidateThreshold && sell >= buy + 8)
        {
            return TSignalState.SellCandidate;
        }

        if (buy >= watchThreshold && buy >= sell + 8)
        {
            return TSignalState.WatchBuy;
        }

        if (sell >= watchThreshold && sell >= buy + 8)
        {
            return TSignalState.WatchSell;
        }

        return TSignalState.Wait;
    }

    private static string BuildSummary(
        TSignalState state,
        int buy,
        int sell,
        bool hasTradingRange,
        bool flowReliable,
        IReadOnlyList<TScoreEvidence> evidence)
    {
        if (!hasTradingRange)
        {
            return "波动空间不足，暂不适合做 T";
        }

        if (state == TSignalState.Volatile)
        {
            return "买卖条件同时偏高，方向不稳定";
        }

        var side = state is TSignalState.BuyCandidate or TSignalState.WatchBuy
            ? TScoreSide.Buy
            : state is TSignalState.SellCandidate or TSignalState.WatchSell
                ? TScoreSide.Sell
                : buy >= sell ? TScoreSide.Buy : TScoreSide.Sell;
        var details = evidence
            .Where(item => item.Side == side)
            .OrderByDescending(item => item.Points)
            .Select(item => item.Description)
            .Distinct()
            .Take(2)
            .ToArray();
        if (details.Length == 0)
        {
            return flowReliable ? "量价条件不足，继续观察" : "量速预热中，继续观察";
        }

        var prefix = state switch
        {
            TSignalState.BuyCandidate => "低吸候选",
            TSignalState.SellCandidate => "高抛候选",
            TSignalState.WatchBuy => "关注低吸",
            TSignalState.WatchSell => "关注高抛",
            _ => "等待"
        };
        return $"{prefix} · {string.Join("、", details)}";
    }
}
