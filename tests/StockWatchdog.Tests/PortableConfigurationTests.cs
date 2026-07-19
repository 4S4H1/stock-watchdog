using StockWatchdog.Application.Configuration;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Market;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.Tests;

public sealed class PortableConfigurationTests
{
    [Fact]
    public void Portable_text_round_trips_settings_watchlist_rules_and_themes()
    {
        var now = TestData.At(10, 0);
        var watchItem = new WatchItem(
            TestData.Stock,
            "贵州茅台",
            0,
            CustomName: "自定义名称");
        var rule = new AlertRule(
            Guid.NewGuid(),
            TestData.Stock,
            AlertRuleType.PriceBelow,
            "低价提醒",
            1_500m,
            null,
            true,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(5),
            3,
            AlertPriority.Normal,
            "1",
            now);
        var bundle = new PortableConfigurationBundle(
            PortableConfigurationCodec.CurrentFormatVersion,
            "StockWatchdog",
            now,
            new AppSettings(
                ThemeId: "dark",
                TScoreAlertThreshold: 82,
                TScoreCooldownMinutes: 15),
            [watchItem],
            [rule],
            [ThemeDefinition.BuiltIns[1] with { Id = "portable-dark", Name = "便携深色" }]);

        var text = PortableConfigurationCodec.Encode(bundle);
        var decoded = PortableConfigurationCodec.Decode(text);

        Assert.StartsWith("SWCFG1.", text, StringComparison.Ordinal);
        Assert.Equal("dark", decoded.Settings.ThemeId);
        Assert.Equal(82, decoded.Settings.TScoreAlertThreshold);
        Assert.Equal(watchItem, Assert.Single(decoded.WatchItems));
        Assert.Equal(rule, Assert.Single(decoded.AlertRules));
        Assert.Equal("portable-dark", Assert.Single(decoded.CustomThemes).Id);
    }

    [Fact]
    public void Corrupted_portable_text_is_rejected()
    {
        var text = PortableConfigurationCodec.Encode(EmptyBundle());
        var firstSeparator = text.IndexOf('.');
        var corruptedCharacters = text.ToCharArray();
        var checksumCharacterIndex = firstSeparator + 1;
        corruptedCharacters[checksumCharacterIndex] =
            corruptedCharacters[checksumCharacterIndex] == 'A' ? 'B' : 'A';
        var corrupted = new string(corruptedCharacters);

        Assert.Throws<InvalidDataException>(() =>
            PortableConfigurationCodec.Decode(corrupted));
    }

    [Fact]
    public void Rule_for_instrument_missing_from_watchlist_is_rejected()
    {
        var now = TestData.At(10, 0);
        var rule = new AlertRule(
            Guid.NewGuid(),
            TestData.Stock,
            AlertRuleType.PriceAbove,
            "无效提醒",
            1m,
            null,
            true,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1),
            1,
            AlertPriority.Normal,
            "1",
            now);
        var invalid = EmptyBundle() with { AlertRules = [rule] };

        Assert.Throws<InvalidDataException>(() =>
            PortableConfigurationCodec.Encode(invalid));
    }

    private static PortableConfigurationBundle EmptyBundle() =>
        new(
            PortableConfigurationCodec.CurrentFormatVersion,
            "StockWatchdog",
            TestData.At(10, 0),
            new AppSettings(),
            [],
            [],
            []);
}
