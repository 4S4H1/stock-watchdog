using StockWatchdog.Application.Abstractions;

namespace StockWatchdog.Application.Services;

public sealed class SystemClock : IClock
{
    private static readonly TimeZoneInfo ChinaTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai");

    public DateTimeOffset Now =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ChinaTimeZone);
}
