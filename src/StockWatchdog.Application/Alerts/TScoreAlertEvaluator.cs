using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Analysis;

namespace StockWatchdog.Application.Alerts;

public sealed class TScoreAlertEvaluator
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(StockWatchdog.Domain.Market.InstrumentId, TScoreSide), AlertState>
        _states = [];

    public void RestoreState(IReadOnlyList<AlertEvent> events)
    {
        lock (_gate)
        {
            _states.Clear();
            foreach (var alert in events.Where(item =>
                         item.Type is AlertRuleType.TScoreBuy or AlertRuleType.TScoreSell))
            {
                var side = alert.Type == AlertRuleType.TScoreBuy
                    ? TScoreSide.Buy
                    : TScoreSide.Sell;
                var key = (alert.Instrument, side);
                if (!_states.TryGetValue(key, out var state)
                    || state.LastTriggered is null
                    || alert.TriggeredAt > state.LastTriggered)
                {
                    _states[key] = new AlertState(
                        0,
                        true,
                        alert.TriggeredAt,
                        default);
                }
            }
        }
    }

    public IReadOnlyList<AlertEvent> Evaluate(
        TSignalSnapshot snapshot,
        bool enabled,
        int threshold,
        TimeSpan cooldown,
        DateTimeOffset now)
    {
        if (!enabled || !snapshot.IsAvailable)
        {
            return [];
        }

        threshold = Math.Clamp(threshold, 60, 95);
        cooldown = TimeSpan.FromMinutes(Math.Clamp(cooldown.TotalMinutes, 1, 240));
        var events = new List<AlertEvent>();
        lock (_gate)
        {
            EvaluateSide(TScoreSide.Buy, snapshot.BuyScore!.Value, snapshot.SellScore!.Value);
            EvaluateSide(TScoreSide.Sell, snapshot.SellScore!.Value, snapshot.BuyScore!.Value);
        }

        return events;

        void EvaluateSide(TScoreSide side, int score, int oppositeScore)
        {
            var key = (snapshot.Instrument, side);
            var state = _states.GetValueOrDefault(key) ?? new AlertState();
            if (state.LastObservedAt == snapshot.CalculatedAt)
            {
                return;
            }

            var condition = score >= threshold && score >= oppositeScore + 8;
            if (!condition)
            {
                var reset = score < threshold - 5;
                _states[key] = state with
                {
                    Consecutive = reset ? 0 : state.Consecutive,
                    AlertedForRun = reset ? false : state.AlertedForRun,
                    LastObservedAt = snapshot.CalculatedAt
                };
                return;
            }

            var consecutive = state.Consecutive + 1;
            var canTrigger = consecutive >= 2
                             && !state.AlertedForRun
                             && (state.LastTriggered is null
                                 || now - state.LastTriggered >= cooldown);
            _states[key] = state with
            {
                Consecutive = consecutive,
                AlertedForRun = state.AlertedForRun || canTrigger,
                LastTriggered = canTrigger ? now : state.LastTriggered,
                LastObservedAt = snapshot.CalculatedAt
            };
            if (!canTrigger)
            {
                return;
            }

            var buy = side == TScoreSide.Buy;
            var minuteKey = now.ToUnixTimeSeconds() / 60;
            events.Add(new AlertEvent(
                Guid.NewGuid(),
                null,
                snapshot.Instrument,
                buy ? AlertRuleType.TScoreBuy : AlertRuleType.TScoreSell,
                AlertPriority.High,
                buy ? $"低吸候选 · 买入条件 {score} 分" : $"高抛候选 · 卖出条件 {score} 分",
                $"{snapshot.Summary}；另一方向 {oppositeScore} 分。分数表示条件匹配强度，不代表成功概率。",
                now,
                snapshot.ValidUntil,
                $"t-score:{snapshot.Instrument}:{side}:{minuteKey}",
                null));
        }
    }

    private sealed record AlertState(
        int Consecutive = 0,
        bool AlertedForRun = false,
        DateTimeOffset? LastTriggered = null,
        DateTimeOffset LastObservedAt = default);
}
