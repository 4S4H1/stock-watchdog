using StockWatchdog.Application.Alerts;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Tests;

public sealed class AlertEvaluatorTests
{
    [Fact]
    public void Quote_rule_triggers_only_on_a_fresh_threshold_crossing()
    {
        var evaluator = new AlertEvaluator();
        var now = TestData.At(10, 0);
        var rule = Rule(now);

        Assert.Empty(evaluator.EvaluateQuote(Quote(9.9m, now), [rule], now));
        Assert.Single(evaluator.EvaluateQuote(Quote(10m, now.AddSeconds(1)), [rule], now.AddSeconds(1)));
        Assert.Empty(evaluator.EvaluateQuote(Quote(10.1m, now.AddSeconds(2)), [rule], now.AddSeconds(2)));
        Assert.Empty(evaluator.EvaluateQuote(Quote(9.9m, now.AddMinutes(1)), [rule], now.AddMinutes(1)));
        Assert.Empty(evaluator.EvaluateQuote(Quote(10m, now.AddMinutes(2)), [rule], now.AddMinutes(2)));

        Assert.Empty(evaluator.EvaluateQuote(Quote(9.9m, now.AddMinutes(11)), [rule], now.AddMinutes(11)));
        Assert.Single(evaluator.EvaluateQuote(Quote(10m, now.AddMinutes(11).AddSeconds(1)), [rule], now.AddMinutes(11).AddSeconds(1)));
    }

    [Fact]
    public void Stale_quote_never_generates_a_new_signal()
    {
        var evaluator = new AlertEvaluator();
        var now = TestData.At(10, 0);
        var rule = Rule(now);

        var stale = Quote(11m, now) with { Quality = MarketDataQuality.Stale };

        Assert.Empty(evaluator.EvaluateQuote(stale, [rule], now));
        Assert.Single(evaluator.EvaluateQuote(Quote(11m, now.AddSeconds(1)), [rule], now.AddSeconds(1)));
    }

    [Fact]
    public void Restored_alert_state_prevents_restart_bombardment_until_condition_resets()
    {
        var evaluator = new AlertEvaluator();
        var now = TestData.At(10, 0);
        var rule = Rule(now);
        var prior = new AlertEvent(
            Guid.NewGuid(),
            rule.Id,
            TestData.Stock,
            rule.Type,
            rule.Priority,
            "prior",
            "prior",
            now.AddMinutes(-20),
            now,
            "prior-key",
            null);
        evaluator.RestoreState([rule], [prior]);

        Assert.Empty(evaluator.EvaluateQuote(Quote(11m, now), [rule], now));
        Assert.Empty(evaluator.EvaluateQuote(Quote(9m, now.AddSeconds(1)), [rule], now.AddSeconds(1)));
        Assert.Single(evaluator.EvaluateQuote(Quote(11m, now.AddSeconds(2)), [rule], now.AddSeconds(2)));
    }

    private static AlertRule Rule(DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            TestData.Stock,
            AlertRuleType.PriceAbove,
            "价格上穿",
            10m,
            null,
            true,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(5),
            2,
            AlertPriority.Normal,
            "1",
            now);

    private static QuoteSnapshot Quote(decimal price, DateTimeOffset now) =>
        new(
            TestData.Stock,
            "测试股票",
            price,
            9.8m,
            price - 9.8m,
            (price / 9.8m - 1m) * 100m,
            1_000,
            price * 1_000m,
            now,
            now,
            "fixture",
            MarketDataQuality.Healthy);
}
