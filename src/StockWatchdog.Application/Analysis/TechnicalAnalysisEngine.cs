using StockWatchdog.Application.Abstractions;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Application.Analysis;

public sealed class TechnicalAnalysisEngine : ITechnicalAnalysisEngine
{
    public const string Version = "1.0.0";

    public AnalysisSnapshot Analyze(
        InstrumentId instrument,
        Timeframe timeframe,
        IReadOnlyList<Bar> bars,
        DateTimeOffset now)
    {
        var finalized = bars
            .Where(x => x.Instrument == instrument
                        && x.Timeframe == timeframe
                        && x.IsFinal
                        && BarIntegrity.HasValidPrices(x))
            .OrderBy(x => x.StartTime)
            .TakeLast(500)
            .ToArray();

        if (finalized.Length < 26)
        {
            return AnalysisSnapshot.WarmingUp(instrument, timeframe, now);
        }

        var indicators = IndicatorCalculator.Calculate(finalized);
        var latest = indicators[^1];
        if (latest.Atr14 is null || latest.Ema20 is null || latest.Rsi14 is null)
        {
            return AnalysisSnapshot.WarmingUp(instrument, timeframe, now);
        }

        var regime = DetectRegime(indicators);
        var findings = new List<PatternFinding>();
        AddTrendFinding(findings, finalized, indicators, regime, now);
        AddBreakoutFinding(findings, finalized, indicators, now);
        AddPullbackFinding(findings, finalized, indicators, regime, now);
        AddMeanReversionFinding(findings, finalized, indicators, regime, now);
        AddCandlestickFindings(findings, finalized, indicators, now);

        var flags = finalized.TakeLast(21).Aggregate(
            DataQualityFlags.None,
            (current, bar) => current | bar.QualityFlags);
        var quality = flags == DataQualityFlags.None
            ? MarketDataQuality.Healthy
            : MarketDataQuality.Delayed;

        return new AnalysisSnapshot(
            instrument,
            timeframe,
            regime,
            finalized,
            indicators,
            findings.OrderByDescending(x => x.Score).ToArray(),
            now,
            quality,
            quality == MarketDataQuality.Healthy
                ? findings.Count == 0
                    ? "暂未发现显著形态"
                    : $"发现 {findings.Count} 项技术观察"
                : "数据质量异常，已暂停技术信号");
    }

    private static MarketRegime DetectRegime(IReadOnlyList<IndicatorPoint> points)
    {
        var latest = points[^1];
        var previous = points[^6];
        if (latest.Ema5 is not { } ema5
            || latest.Ema10 is not { } ema10
            || latest.Ema20 is not { } ema20
            || previous.Ema20 is not { } previousEma20
            || latest.Atr14 is not { } atr
            || atr <= 0)
        {
            return MarketRegime.Unknown;
        }

        var normalizedSlope = (ema20 - previousEma20) / atr;
        if (ema5 > ema10 && ema10 > ema20 && normalizedSlope >= 0.15m)
        {
            return MarketRegime.TrendingUp;
        }

        if (ema5 < ema10 && ema10 < ema20 && normalizedSlope <= -0.15m)
        {
            return MarketRegime.TrendingDown;
        }

        return MarketRegime.Ranging;
    }

    private static void AddTrendFinding(
        ICollection<PatternFinding> findings,
        IReadOnlyList<Bar> bars,
        IReadOnlyList<IndicatorPoint> points,
        MarketRegime regime,
        DateTimeOffset now)
    {
        if (regime is not (MarketRegime.TrendingUp or MarketRegime.TrendingDown))
        {
            return;
        }

        var latest = points[^1];
        var bullish = regime == MarketRegime.TrendingUp;
        var evidence = new[]
        {
            new PatternEvidence(
                "EMA5",
                latest.Ema5!.Value,
                bullish ? ">" : "<",
                latest.Ema10!.Value,
                "元",
                $"EMA5 {(bullish ? "高于" : "低于")} EMA10"),
            new PatternEvidence(
                "EMA10",
                latest.Ema10.Value,
                bullish ? ">" : "<",
                latest.Ema20!.Value,
                "元",
                $"EMA10 {(bullish ? "高于" : "低于")} EMA20")
        };

        findings.Add(CreateFinding(
            bars,
            "trend-alignment",
            bullish ? "均线多头排列" : "均线空头排列",
            bullish ? PatternDirection.Bullish : PatternDirection.Bearish,
            75,
            now,
            evidence,
            bullish
                ? "短中期均线依次向上，当前处于偏强趋势背景。"
                : "短中期均线依次向下，当前处于偏弱趋势背景。",
            bullish
                ? $"EMA10 跌破 EMA20 或收盘低于 {latest.Ema20:0.000}"
                : $"EMA10 上穿 EMA20 或收盘高于 {latest.Ema20:0.000}"));
    }

    private static void AddBreakoutFinding(
        ICollection<PatternFinding> findings,
        IReadOnlyList<Bar> bars,
        IReadOnlyList<IndicatorPoint> points,
        DateTimeOffset now)
    {
        var latestBar = bars[^1];
        var latest = points[^1];
        var previousWindow = bars.Skip(bars.Count - 21).Take(20).ToArray();
        var previousHigh = previousWindow.Max(x => x.High);
        var previousLow = previousWindow.Min(x => x.Low);
        var atr = latest.Atr14!.Value;
        var volumeRatio = latest.VolumeRatio ?? 0;

        if (latestBar.Close > previousHigh + atr * 0.1m && volumeRatio >= 1.8m)
        {
            findings.Add(CreateFinding(
                bars,
                "volume-breakout-up",
                "放量向上突破",
                PatternDirection.Bullish,
                85,
                now,
                [
                    new("收盘价", latestBar.Close, ">", previousHigh + atr * 0.1m, "元", "收盘突破前 20 根 K 线区间"),
                    new("量比", volumeRatio, ">=", 1.8m, "倍", "成交量显著高于 20 期均量")
                ],
                $"收盘价 {latestBar.Close:0.000} 放量突破区间上沿 {previousHigh:0.000}。",
                $"收盘重新跌回 {previousHigh:0.000} 以下"));
        }
        else if (latestBar.Close < previousLow - atr * 0.1m && volumeRatio >= 1.8m)
        {
            findings.Add(CreateFinding(
                bars,
                "volume-breakout-down",
                "放量向下突破",
                PatternDirection.Bearish,
                85,
                now,
                [
                    new("收盘价", latestBar.Close, "<", previousLow - atr * 0.1m, "元", "收盘跌破前 20 根 K 线区间"),
                    new("量比", volumeRatio, ">=", 1.8m, "倍", "成交量显著高于 20 期均量")
                ],
                $"收盘价 {latestBar.Close:0.000} 放量跌破区间下沿 {previousLow:0.000}。",
                $"收盘重新站上 {previousLow:0.000}"));
        }
    }

    private static void AddPullbackFinding(
        ICollection<PatternFinding> findings,
        IReadOnlyList<Bar> bars,
        IReadOnlyList<IndicatorPoint> points,
        MarketRegime regime,
        DateTimeOffset now)
    {
        if (bars.Count < 30 || regime == MarketRegime.Ranging)
        {
            return;
        }

        var latest = bars[^1];
        var indicator = points[^1];
        var atr = indicator.Atr14!.Value;
        var baseWindow = bars.Skip(bars.Count - 26).Take(20).ToArray();
        var recent = bars.Skip(bars.Count - 6).Take(5).ToArray();
        var rangeHigh = baseWindow.Max(x => x.High);
        var rangeLow = baseWindow.Min(x => x.Low);

        if (regime == MarketRegime.TrendingUp
            && recent.Any(x => x.Close > rangeHigh)
            && Math.Abs(latest.Low - rangeHigh) <= atr * 0.3m
            && latest.Close >= rangeHigh
            && (indicator.VolumeRatio ?? 1m) <= 1.2m)
        {
            findings.Add(CreateFinding(
                bars,
                "breakout-pullback-up",
                "突破后缩量回踩",
                PatternDirection.Bullish,
                78,
                now,
                [
                    new("回踩低点", latest.Low, "~", rangeHigh, "元", "低点接近原区间上沿"),
                    new("收盘价", latest.Close, ">=", rangeHigh, "元", "收盘仍守住突破位置")
                ],
                "向上突破后回踩原区间上沿，当前仍守住关键位置。",
                $"收盘跌破 {rangeHigh - atr * 0.2m:0.000}"));
        }
        else if (regime == MarketRegime.TrendingDown
                 && recent.Any(x => x.Close < rangeLow)
                 && Math.Abs(latest.High - rangeLow) <= atr * 0.3m
                 && latest.Close <= rangeLow
                 && (indicator.VolumeRatio ?? 1m) <= 1.2m)
        {
            findings.Add(CreateFinding(
                bars,
                "breakout-pullback-down",
                "跌破后缩量反抽",
                PatternDirection.Bearish,
                78,
                now,
                [
                    new("反抽高点", latest.High, "~", rangeLow, "元", "高点接近原区间下沿"),
                    new("收盘价", latest.Close, "<=", rangeLow, "元", "收盘仍受制于跌破位置")
                ],
                "向下跌破后反抽原区间下沿，当前仍未收复关键位置。",
                $"收盘站上 {rangeLow + atr * 0.2m:0.000}"));
        }
    }

    private static void AddMeanReversionFinding(
        ICollection<PatternFinding> findings,
        IReadOnlyList<Bar> bars,
        IReadOnlyList<IndicatorPoint> points,
        MarketRegime regime,
        DateTimeOffset now)
    {
        if (regime != MarketRegime.Ranging)
        {
            return;
        }

        var latestBar = bars[^1];
        var latest = points[^1];
        if (latest.Vwap is not { } vwap || latest.Atr14 is not { } atr || latest.Rsi14 is not { } rsi)
        {
            return;
        }

        var deviation = latestBar.Close - vwap;
        if (deviation <= -1.2m * atr && rsi <= 30m)
        {
            findings.Add(CreateFinding(
                bars,
                "vwap-mean-reversion-low",
                "VWAP 下方过度偏离",
                PatternDirection.Bullish,
                80,
                now,
                [
                    new("VWAP偏离", deviation, "<=", -1.2m * atr, "元", "价格显著低于当日成交均价"),
                    new("RSI14", rsi, "<=", 30m, string.Empty, "短周期动量进入超卖区域")
                ],
                $"震荡背景下价格低于 VWAP {Math.Abs(deviation):0.000}，存在均值回归观察条件。",
                $"继续跌破 {latestBar.Close - atr * 0.6m:0.000}"));
        }
        else if (deviation >= 1.2m * atr && rsi >= 70m)
        {
            findings.Add(CreateFinding(
                bars,
                "vwap-mean-reversion-high",
                "VWAP 上方过度偏离",
                PatternDirection.Bearish,
                80,
                now,
                [
                    new("VWAP偏离", deviation, ">=", 1.2m * atr, "元", "价格显著高于当日成交均价"),
                    new("RSI14", rsi, ">=", 70m, string.Empty, "短周期动量进入超买区域")
                ],
                $"震荡背景下价格高于 VWAP {deviation:0.000}，存在均值回归观察条件。",
                $"继续突破 {latestBar.Close + atr * 0.6m:0.000}"));
        }
    }

    private static void AddCandlestickFindings(
        ICollection<PatternFinding> findings,
        IReadOnlyList<Bar> bars,
        IReadOnlyList<IndicatorPoint> points,
        DateTimeOffset now)
    {
        var latest = bars[^1];
        var previous = bars[^2];
        var range = latest.High - latest.Low;
        if (range <= 0)
        {
            return;
        }

        var body = Math.Abs(latest.Close - latest.Open);
        if (body / range <= 0.1m)
        {
            findings.Add(CreateFinding(
                bars,
                "candlestick-doji",
                "十字线",
                PatternDirection.Neutral,
                45,
                now,
                [new("实体占比", body / range, "<=", 0.1m, string.Empty, "开收盘接近，显示短期犹豫")],
                "当前 K 线实体很小，多空暂时均衡；该形态仅作辅助观察。",
                "下一根 K 线完成后重新评估"));
        }

        var lowerShadow = Math.Min(latest.Open, latest.Close) - latest.Low;
        var upperShadow = latest.High - Math.Max(latest.Open, latest.Close);
        if (lowerShadow >= body * 2m && lowerShadow > upperShadow * 1.5m)
        {
            findings.Add(CreateFinding(
                bars,
                "candlestick-hammer",
                "长下影锤头",
                PatternDirection.Bullish,
                55,
                now,
                [new("下影/实体", body == 0 ? 99m : lowerShadow / body, ">=", 2m, "倍", "下方承接明显")],
                "长下影显示盘中下探后被拉回；需要趋势和成交量进一步确认。",
                $"跌破本 K 线低点 {latest.Low:0.000}"));
        }

        var bullishEngulfing = previous.Close < previous.Open
            && latest.Close > latest.Open
            && latest.Open <= previous.Close
            && latest.Close >= previous.Open;
        var bearishEngulfing = previous.Close > previous.Open
            && latest.Close < latest.Open
            && latest.Open >= previous.Close
            && latest.Close <= previous.Open;
        if (bullishEngulfing || bearishEngulfing)
        {
            findings.Add(CreateFinding(
                bars,
                bullishEngulfing ? "candlestick-engulfing-up" : "candlestick-engulfing-down",
                bullishEngulfing ? "看涨吞没" : "看跌吞没",
                bullishEngulfing ? PatternDirection.Bullish : PatternDirection.Bearish,
                60,
                now,
                [
                    new(
                        "当前实体",
                        Math.Abs(latest.Close - latest.Open),
                        ">=",
                        Math.Abs(previous.Close - previous.Open),
                        "元",
                        "当前实体覆盖前一根实体")
                ],
                bullishEngulfing
                    ? "阳线实体覆盖前一根阴线实体，仅作为反转辅助证据。"
                    : "阴线实体覆盖前一根阳线实体，仅作为反转辅助证据。",
                bullishEngulfing
                    ? $"跌破 {latest.Low:0.000}"
                    : $"突破 {latest.High:0.000}"));
        }
    }

    private static PatternFinding CreateFinding(
        IReadOnlyList<Bar> bars,
        string patternId,
        string displayName,
        PatternDirection direction,
        int score,
        DateTimeOffset now,
        IReadOnlyList<PatternEvidence> evidence,
        string rationale,
        string invalidation)
    {
        var latest = bars[^1];
        return new PatternFinding(
            latest.Instrument,
            latest.Timeframe,
            patternId,
            displayName,
            Version,
            direction,
            Math.Clamp(score, 0, 100),
            latest.EndTime,
            latest.EndTime + TimeSpan.FromMinutes(Math.Max(5, (int)latest.Timeframe * 2)),
            latest.IsFinal,
            evidence,
            rationale,
            invalidation,
            bars.TakeLast(21).Select(x => x.Id).ToArray(),
            latest.Source,
            now - latest.EndTime,
            bars.TakeLast(21).Aggregate(
                DataQualityFlags.None,
                (current, bar) => current | bar.QualityFlags));
    }
}
