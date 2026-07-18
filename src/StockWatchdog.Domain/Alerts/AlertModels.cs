using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.Domain.Alerts;

public enum AlertRuleType
{
    PriceAbove,
    PriceBelow,
    ChangePercentAbove,
    ChangePercentBelow,
    Pattern
}

public enum AlertPriority
{
    Information,
    Normal,
    High,
    Critical
}

public enum AlertOutcome
{
    Pending,
    Hit,
    Failed,
    Expired,
    NonExecutable,
    NotAdopted
}

public sealed record AlertRule(
    Guid Id,
    InstrumentId Instrument,
    AlertRuleType Type,
    string Name,
    decimal? Threshold,
    string? PatternId,
    bool Enabled,
    TimeSpan Cooldown,
    TimeSpan ValidFor,
    int MaxTriggersPerDay,
    AlertPriority Priority,
    string Version,
    DateTimeOffset CreatedAt);

public sealed record AlertEvent(
    Guid Id,
    Guid? RuleId,
    InstrumentId Instrument,
    AlertRuleType Type,
    AlertPriority Priority,
    string Title,
    string Message,
    DateTimeOffset TriggeredAt,
    DateTimeOffset ValidUntil,
    string DeduplicationKey,
    PatternFinding? Finding,
    AlertOutcome Outcome = AlertOutcome.Pending);
