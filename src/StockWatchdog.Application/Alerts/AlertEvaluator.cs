using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Application.Alerts;

public sealed class AlertEvaluator
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastTriggered = [];
    private readonly Dictionary<(Guid RuleId, DateOnly Date), int> _dailyCounts = [];
    private readonly Dictionary<Guid, bool> _lastCondition = [];
    private readonly HashSet<string> _deduplicationKeys = new(StringComparer.Ordinal);

    public void RestoreState(
        IReadOnlyList<AlertRule> rules,
        IReadOnlyList<AlertEvent> events)
    {
        var rulesById = rules.ToDictionary(x => x.Id);
        lock (_gate)
        {
            _lastTriggered.Clear();
            _dailyCounts.Clear();
            _lastCondition.Clear();
            _deduplicationKeys.Clear();
            foreach (var alert in events.Where(x => x.RuleId is not null))
            {
                var ruleId = alert.RuleId!.Value;
                if (!rulesById.TryGetValue(ruleId, out var rule))
                {
                    continue;
                }

                if (!_lastTriggered.TryGetValue(ruleId, out var previous)
                    || alert.TriggeredAt > previous)
                {
                    _lastTriggered[ruleId] = alert.TriggeredAt;
                }

                var key = (ruleId, DateOnly.FromDateTime(alert.TriggeredAt.LocalDateTime));
                _dailyCounts[key] = _dailyCounts.GetValueOrDefault(key) + 1;
                _deduplicationKeys.Add(alert.DeduplicationKey);
                if (rule.Type is AlertRuleType.PriceAbove
                    or AlertRuleType.PriceBelow
                    or AlertRuleType.ChangePercentAbove
                    or AlertRuleType.ChangePercentBelow)
                {
                    _lastCondition[ruleId] = true;
                }
            }
        }
    }

    public IReadOnlyList<AlertEvent> EvaluateQuote(
        QuoteSnapshot quote,
        IReadOnlyList<AlertRule> rules,
        DateTimeOffset now)
    {
        if (quote.Quality is not MarketDataQuality.Healthy)
        {
            return [];
        }

        var events = new List<AlertEvent>();
        lock (_gate)
        {
            foreach (var rule in rules.Where(x => x.Enabled && x.Instrument == quote.Instrument))
            {
                var condition = MatchesQuote(rule, quote);
                var previousCondition = _lastCondition.GetValueOrDefault(rule.Id);
                _lastCondition[rule.Id] = condition;
                if (!condition || previousCondition || !CanTrigger(rule, now))
                {
                    continue;
                }

                var alert = CreateEvent(
                    rule,
                    now,
                    $"{quote.Name} 条件满足",
                    BuildQuoteMessage(rule, quote),
                    null);
                events.Add(alert);
                _deduplicationKeys.Add(alert.DeduplicationKey);
                RecordTrigger(rule, now);
            }
        }

        return events;
    }

    public IReadOnlyList<AlertEvent> EvaluateAnalysis(
        AnalysisSnapshot analysis,
        IReadOnlyList<AlertRule> rules,
        DateTimeOffset now)
    {
        if (analysis.DataQuality is not MarketDataQuality.Healthy)
        {
            return [];
        }

        var events = new List<AlertEvent>();
        lock (_gate)
        {
            foreach (var rule in rules.Where(
                         x => x.Enabled
                              && x.Instrument == analysis.Instrument
                              && x.Type == AlertRuleType.Pattern))
            {
                var finding = analysis.Findings.FirstOrDefault(
                    x => x.PatternId == rule.PatternId && x.ValidUntil >= now);
                var deduplicationKey = finding is null
                    ? null
                    : $"{rule.Id:N}:{finding.DetectedAt.UtcTicks}";
                if (finding is null
                    || deduplicationKey is null
                    || _deduplicationKeys.Contains(deduplicationKey)
                    || !CanTrigger(rule, now))
                {
                    continue;
                }

                var alert = CreateEvent(
                    rule,
                    now,
                    $"{finding.DisplayName} 条件满足",
                    finding.Rationale,
                    finding);
                events.Add(alert);
                _deduplicationKeys.Add(alert.DeduplicationKey);
                RecordTrigger(rule, now);
            }
        }

        return events;
    }

    private static bool MatchesQuote(AlertRule rule, QuoteSnapshot quote) =>
        rule.Threshold is { } threshold
        && rule.Type switch
        {
            AlertRuleType.PriceAbove => quote.Price >= threshold,
            AlertRuleType.PriceBelow => quote.Price <= threshold,
            AlertRuleType.ChangePercentAbove => quote.ChangePercent >= threshold,
            AlertRuleType.ChangePercentBelow => quote.ChangePercent <= threshold,
            _ => false
        };

    private bool CanTrigger(AlertRule rule, DateTimeOffset now)
    {
        if (_lastTriggered.TryGetValue(rule.Id, out var last)
            && now - last < rule.Cooldown)
        {
            return false;
        }

        var key = (rule.Id, DateOnly.FromDateTime(now.LocalDateTime));
        return _dailyCounts.GetValueOrDefault(key) < rule.MaxTriggersPerDay;
    }

    private void RecordTrigger(AlertRule rule, DateTimeOffset now)
    {
        _lastTriggered[rule.Id] = now;
        var key = (rule.Id, DateOnly.FromDateTime(now.LocalDateTime));
        _dailyCounts[key] = _dailyCounts.GetValueOrDefault(key) + 1;
    }

    private static AlertEvent CreateEvent(
        AlertRule rule,
        DateTimeOffset now,
        string title,
        string message,
        PatternFinding? finding) =>
        new(
            Guid.NewGuid(),
            rule.Id,
            rule.Instrument,
            rule.Type,
            rule.Priority,
            title,
            message,
            now,
            now + rule.ValidFor,
            $"{rule.Id:N}:{finding?.DetectedAt.UtcTicks ?? now.UtcTicks}",
            finding);

    private static string BuildQuoteMessage(AlertRule rule, QuoteSnapshot quote) =>
        rule.Type switch
        {
            AlertRuleType.PriceAbove => $"现价 {quote.Price:0.000} 已达到或高于 {rule.Threshold:0.000}",
            AlertRuleType.PriceBelow => $"现价 {quote.Price:0.000} 已达到或低于 {rule.Threshold:0.000}",
            AlertRuleType.ChangePercentAbove => $"今日涨跌幅 {quote.ChangePercent:+0.00;-0.00;0.00}% 已达到设定值",
            AlertRuleType.ChangePercentBelow => $"今日涨跌幅 {quote.ChangePercent:+0.00;-0.00;0.00}% 已达到设定值",
            _ => rule.Name
        };
}
