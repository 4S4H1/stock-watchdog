using Microsoft.Data.Sqlite;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;
using StockWatchdog.Domain.Settings;
using StockWatchdog.Infrastructure.Persistence;

namespace StockWatchdog.Tests;

public sealed class SqliteRepositoryTests
{
    [Fact]
    public async Task Portable_configuration_replace_is_atomic_and_preserves_history()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"stock-watchdog-portable-{Guid.NewGuid():N}");
        try
        {
            var repository = new SqliteAppRepository(Path.Combine(directory, "state.db"));
            await repository.InitializeAsync();
            var now = TestData.At(10, 0);
            var originalItem = new WatchItem(TestData.Stock, "原标的", 0);
            var originalRule = new AlertRule(
                Guid.NewGuid(),
                TestData.Stock,
                AlertRuleType.PriceAbove,
                "原规则",
                10m,
                null,
                true,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(2),
                3,
                AlertPriority.Normal,
                "1",
                now);
            var history = new AlertEvent(
                Guid.NewGuid(),
                originalRule.Id,
                TestData.Stock,
                AlertRuleType.PriceAbove,
                AlertPriority.Normal,
                "历史提醒",
                "保留",
                now,
                now.AddMinutes(2),
                "portable-history",
                null);
            await repository.SaveSettingsAsync(new AppSettings(ThemeId: "light"));
            await repository.UpsertWatchItemAsync(originalItem);
            await repository.UpsertAlertRuleAsync(originalRule);
            Assert.True(await repository.TryAddAlertEventAsync(history));

            _ = InstrumentId.TryParse("510300", out var importedInstrument);
            var importedItem = new WatchItem(importedInstrument, "沪深300ETF", 0);
            var importedRule = originalRule with
            {
                Id = Guid.NewGuid(),
                Instrument = importedInstrument,
                Name = "导入规则"
            };
            await repository.ReplacePortableConfigurationAsync(
                new AppSettings(ThemeId: "dark"),
                [importedItem],
                [importedRule]);

            Assert.Equal("dark", (await repository.GetSettingsAsync()).ThemeId);
            Assert.Equal(importedItem, Assert.Single(await repository.GetWatchItemsAsync()));
            Assert.Equal(importedRule, Assert.Single(await repository.GetAlertRulesAsync()));
            Assert.Equal(history, Assert.Single(
                await repository.GetAlertEventsAsync(now.AddMinutes(-1))));

            var duplicateItems = new[]
            {
                importedItem,
                importedItem with { SortOrder = 1, Name = "重复" }
            };
            await Assert.ThrowsAsync<SqliteException>(() =>
                repository.ReplacePortableConfigurationAsync(
                    new AppSettings(ThemeId: "spreadsheet"),
                    duplicateItems,
                    []));

            Assert.Equal("dark", (await repository.GetSettingsAsync()).ThemeId);
            Assert.Equal(importedItem, Assert.Single(await repository.GetWatchItemsAsync()));
            Assert.Equal(importedRule, Assert.Single(await repository.GetAlertRulesAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task Repository_round_trips_core_local_state()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"stock-watchdog-tests-{Guid.NewGuid():N}");
        try
        {
            var repository = new SqliteAppRepository(Path.Combine(directory, "state.db"));
            await repository.InitializeAsync();
            var now = TestData.At(10, 0);
            var settings = new AppSettings(
                RefreshSeconds: 1,
                ThemeId: "spreadsheet",
                CompactMode: CompactDisplayMode.Minimal,
                ShowCodeColumn: false,
                ShowSparklineColumn: false);
            var watchItem = new WatchItem(
                TestData.Stock,
                "贵州茅台",
                0,
                CustomName: "自定义名称");
            var alertRule = new AlertRule(
                Guid.NewGuid(),
                TestData.Stock,
                AlertRuleType.PriceAbove,
                "价格提醒",
                1_600m,
                null,
                true,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(2),
                3,
                AlertPriority.Normal,
                "1",
                now);
            var alertEvent = new AlertEvent(
                Guid.NewGuid(),
                alertRule.Id,
                TestData.Stock,
                AlertRuleType.PriceAbove,
                AlertPriority.Normal,
                "价格提醒",
                "测试",
                now,
                now.AddMinutes(5),
                "stable-dedup-key",
                null);
            var bar = TestData.MinuteBar(now, 1_550m);
            var marker = new ChartTradeMarker(
                Guid.NewGuid(),
                TestData.Stock,
                Timeframe.Minute1,
                ChartTradeSide.Buy,
                now,
                1_548m,
                now);
            var secondMarker = marker with
            {
                Id = Guid.NewGuid(),
                Side = ChartTradeSide.Sell,
                Time = now.AddMinutes(1),
                Price = 1_552m,
                CreatedAt = now.AddMinutes(1)
            };

            await repository.SaveSettingsAsync(settings);
            await repository.UpsertWatchItemAsync(watchItem);
            await repository.UpsertChartTradeMarkerAsync(marker);
            await repository.UpsertChartTradeMarkerAsync(secondMarker);
            await repository.UpsertAlertRuleAsync(alertRule);
            await repository.SaveBarsAsync([bar]);
            Assert.True(await repository.TryAddAlertEventAsync(alertEvent));
            Assert.False(await repository.TryAddAlertEventAsync(alertEvent with { Id = Guid.NewGuid() }));

            var savedSettings = await repository.GetSettingsAsync();
            Assert.Equal(3, savedSettings.RefreshSeconds);
            Assert.Equal(CompactDisplayMode.Minimal, savedSettings.CompactMode);
            Assert.False(savedSettings.ShowCodeColumn);
            Assert.False(savedSettings.ShowSparklineColumn);
            Assert.Equal(watchItem, Assert.Single(await repository.GetWatchItemsAsync()));
            Assert.Equal(
                [marker, secondMarker],
                await repository.GetChartTradeMarkersAsync(
                    TestData.Stock,
                    Timeframe.Minute1));
            Assert.Equal(alertRule, Assert.Single(await repository.GetAlertRulesAsync()));
            Assert.Equal(alertEvent, Assert.Single(await repository.GetAlertEventsAsync(now.AddMinutes(-1))));
            Assert.Equal(bar, Assert.Single(await repository.GetCachedBarsAsync(
                TestData.Stock,
                Timeframe.Minute1,
                10)));

            await repository.DeleteAlertRuleAsync(alertRule.Id);
            await repository.DeleteChartTradeMarkerAsync(marker.Id);
            Assert.Equal(
                secondMarker,
                Assert.Single(await repository.GetChartTradeMarkersAsync(
                    TestData.Stock,
                    Timeframe.Minute1)));
            await repository.DeleteChartTradeMarkersAsync(
                secondMarker.Instrument,
                secondMarker.Timeframe);
            await repository.DeleteWatchItemAsync(TestData.Stock);

            Assert.Empty(await repository.GetAlertRulesAsync());
            Assert.Empty(await repository.GetChartTradeMarkersAsync(
                TestData.Stock,
                Timeframe.Minute1));
            Assert.Empty(await repository.GetWatchItemsAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task Initialization_migrates_alert_table_created_by_an_older_build()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"stock-watchdog-migration-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "state.db");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE alert_events (
                        id TEXT PRIMARY KEY,
                        triggered_at TEXT NOT NULL,
                        json TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqliteAppRepository(databasePath);
            await repository.InitializeAsync();
            var now = TestData.At(10, 0);
            var alert = new AlertEvent(
                Guid.NewGuid(),
                null,
                TestData.Stock,
                AlertRuleType.Pattern,
                AlertPriority.Normal,
                "migration",
                "migration",
                now,
                now.AddMinutes(1),
                "migration-dedup",
                null);

            Assert.True(await repository.TryAddAlertEventAsync(alert));
            Assert.Equal(alert, Assert.Single(await repository.GetAlertEventsAsync(now.AddMinutes(-1))));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task Initialization_removes_legacy_trading_tables_and_data()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"stock-watchdog-legacy-trading-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(directory, "state.db");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE positions (
                        instrument TEXT PRIMARY KEY,
                        json TEXT NOT NULL
                    );
                    CREATE TABLE fee_profile (
                        id INTEGER PRIMARY KEY,
                        json TEXT NOT NULL
                    );
                    CREATE TABLE t_plans (
                        id TEXT PRIMARY KEY,
                        instrument TEXT NOT NULL,
                        status INTEGER NOT NULL,
                        updated_at TEXT NOT NULL,
                        json TEXT NOT NULL
                    );
                    CREATE TABLE manual_executions (
                        id TEXT PRIMARY KEY,
                        plan_id TEXT NOT NULL,
                        executed_at TEXT NOT NULL,
                        json TEXT NOT NULL
                    );
                    CREATE TABLE alert_rules (
                        id TEXT PRIMARY KEY,
                        instrument TEXT NOT NULL,
                        json TEXT NOT NULL
                    );
                    INSERT INTO positions(instrument, json) VALUES('SH:600000:Stock', '{"legacy":true}');
                    INSERT INTO fee_profile(id, json) VALUES(1, '{"legacy":true}');
                    INSERT INTO t_plans(id, instrument, status, updated_at, json)
                        VALUES('plan', 'SH:600000:Stock', 0, '2026-07-18T00:00:00Z', '{"legacy":true}');
                    INSERT INTO manual_executions(id, plan_id, executed_at, json)
                        VALUES('execution', 'plan', '2026-07-18T00:00:00Z', '{"legacy":true}');
                    INSERT INTO alert_rules(id, instrument, json)
                        VALUES('legacy-t', 'SH.600000', '{"type":"TOpportunity"}');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqliteAppRepository(databasePath);
            await repository.InitializeAsync();
            Assert.Empty(await repository.GetAlertRulesAsync());

            await using var verify = new SqliteConnection($"Data Source={databasePath}");
            await verify.OpenAsync();
            foreach (var table in new[]
                     {
                         "positions",
                         "fee_profile",
                         "t_plans",
                         "manual_executions"
                     })
            {
                await using var command = verify.CreateCommand();
                command.CommandText =
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
                command.Parameters.AddWithValue("$name", table);
                Assert.Equal(0L, await command.ExecuteScalarAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
