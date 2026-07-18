using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.Infrastructure.Persistence;

public sealed class SqliteAppRepository : IAppRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _connectionString;

    public SqliteAppRepository(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS watch_items (
                instrument TEXT PRIMARY KEY,
                sort_order INTEGER NOT NULL,
                json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS alert_rules (
                id TEXT PRIMARY KEY,
                instrument TEXT NOT NULL,
                json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chart_trade_markers (
                id TEXT PRIMARY KEY,
                instrument TEXT NOT NULL,
                timeframe INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS alert_events (
                id TEXT PRIMARY KEY,
                triggered_at TEXT NOT NULL,
                dedup_key TEXT NOT NULL,
                json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS bars (
                instrument TEXT NOT NULL,
                timeframe INTEGER NOT NULL,
                start_utc TEXT NOT NULL,
                json TEXT NOT NULL,
                PRIMARY KEY (instrument, timeframe, start_utc)
            );

            CREATE INDEX IF NOT EXISTS ix_bars_lookup
                ON bars(instrument, timeframe, start_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_chart_trade_markers_lookup
                ON chart_trade_markers(instrument, timeframe, created_at);

            DROP TABLE IF EXISTS positions;
            DROP TABLE IF EXISTS fee_profile;
            DROP TABLE IF EXISTS t_plans;
            DROP TABLE IF EXISTS manual_executions;

            DELETE FROM alert_events
            WHERE json LIKE '%TOpportunity%';

            DELETE FROM alert_rules
            WHERE json LIKE '%TOpportunity%';

            DELETE FROM alert_events
            WHERE triggered_at < datetime('now', '-180 days');

            DELETE FROM bars
            WHERE timeframe <> 1440 AND start_utc < datetime('now', '-14 days');

            DELETE FROM bars
            WHERE timeframe = 1440 AND start_utc < datetime('now', '-600 days');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAlertDeduplicationSchemaAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var json = await ReadSingleJsonAsync(
            "SELECT json FROM settings WHERE id = 1",
            cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json)
            ? new AppSettings()
            : Deserialize<AppSettings>(json).Normalize();
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        ExecuteJsonAsync(
            """
            INSERT INTO settings(id, json) VALUES(1, $json)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json
            """,
            [("$json", Serialize(settings.Normalize()))],
            cancellationToken);

    public async Task<IReadOnlyList<WatchItem>> GetWatchItemsAsync(
        CancellationToken cancellationToken = default) =>
        await ReadManyAsync<WatchItem>(
            "SELECT json FROM watch_items ORDER BY sort_order",
            cancellationToken).ConfigureAwait(false);

    public Task UpsertWatchItemAsync(WatchItem item, CancellationToken cancellationToken = default) =>
        ExecuteJsonAsync(
            """
            INSERT INTO watch_items(instrument, sort_order, json)
            VALUES($instrument, $sort_order, $json)
            ON CONFLICT(instrument) DO UPDATE SET
                sort_order = excluded.sort_order,
                json = excluded.json
            """,
            [
                ("$instrument", item.Instrument.ToString()),
                ("$sort_order", item.SortOrder),
                ("$json", Serialize(item))
            ],
            cancellationToken);

    public Task DeleteWatchItemAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken = default) =>
        ExecuteJsonAsync(
            "DELETE FROM watch_items WHERE instrument = $instrument",
            [("$instrument", instrument.ToString())],
            cancellationToken);

    public async Task<IReadOnlyList<ChartTradeMarker>> GetChartTradeMarkersAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT json FROM chart_trade_markers
            WHERE instrument = $instrument AND timeframe = $timeframe
            ORDER BY created_at
            """;
        command.Parameters.AddWithValue("$instrument", instrument.ToString());
        command.Parameters.AddWithValue("$timeframe", (int)timeframe);
        var result = new List<ChartTradeMarker>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Deserialize<ChartTradeMarker>(reader.GetString(0)));
        }

        return result;
    }

    public Task UpsertChartTradeMarkerAsync(
        ChartTradeMarker marker,
        CancellationToken cancellationToken = default) =>
        ExecuteJsonAsync(
            """
            INSERT INTO chart_trade_markers(id, instrument, timeframe, created_at, json)
            VALUES($id, $instrument, $timeframe, $created_at, $json)
            ON CONFLICT(id) DO UPDATE SET
                instrument = excluded.instrument,
                timeframe = excluded.timeframe,
                created_at = excluded.created_at,
                json = excluded.json
            """,
            [
                ("$id", marker.Id.ToString("N")),
                ("$instrument", marker.Instrument.ToString()),
                ("$timeframe", (int)marker.Timeframe),
                ("$created_at", marker.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
                ("$json", Serialize(marker))
            ],
            cancellationToken);

    public Task DeleteChartTradeMarkerAsync(
        Guid markerId,
        CancellationToken cancellationToken = default) =>
        ExecuteJsonAsync(
            "DELETE FROM chart_trade_markers WHERE id = $id",
            [("$id", markerId.ToString("N"))],
            cancellationToken);

    public Task DeleteChartTradeMarkersAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        CancellationToken cancellationToken = default) =>
        ExecuteJsonAsync(
            """
            DELETE FROM chart_trade_markers
            WHERE instrument = $instrument AND timeframe = $timeframe
            """,
            [
                ("$instrument", instrument.ToString()),
                ("$timeframe", (int)timeframe)
            ],
            cancellationToken);

    public async Task<IReadOnlyList<AlertRule>> GetAlertRulesAsync(
        CancellationToken cancellationToken = default) =>
        await ReadManyAsync<AlertRule>(
            "SELECT json FROM alert_rules",
            cancellationToken).ConfigureAwait(false);

    public Task UpsertAlertRuleAsync(AlertRule rule, CancellationToken cancellationToken = default) =>
        ExecuteJsonAsync(
            """
            INSERT INTO alert_rules(id, instrument, json)
            VALUES($id, $instrument, $json)
            ON CONFLICT(id) DO UPDATE SET
                instrument = excluded.instrument,
                json = excluded.json
            """,
            [
                ("$id", rule.Id.ToString("N")),
                ("$instrument", rule.Instrument.ToString()),
                ("$json", Serialize(rule))
            ],
            cancellationToken);

    public Task DeleteAlertRuleAsync(Guid ruleId, CancellationToken cancellationToken = default) =>
        ExecuteJsonAsync(
            "DELETE FROM alert_rules WHERE id = $id",
            [("$id", ruleId.ToString("N"))],
            cancellationToken);

    public async Task<IReadOnlyList<AlertEvent>> GetAlertEventsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT json FROM alert_events
            WHERE triggered_at >= $since
            ORDER BY triggered_at
            """;
        command.Parameters.AddWithValue(
            "$since",
            since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        var result = new List<AlertEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Deserialize<AlertEvent>(reader.GetString(0)));
        }

        return result;
    }

    public async Task<bool> TryAddAlertEventAsync(
        AlertEvent alert,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO alert_events(id, triggered_at, dedup_key, json)
            VALUES($id, $triggered_at, $dedup_key, $json)
            """;
        command.Parameters.AddWithValue("$id", alert.Id.ToString("N"));
        command.Parameters.AddWithValue(
            "$triggered_at",
            alert.TriggeredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$dedup_key", alert.DeduplicationKey);
        command.Parameters.AddWithValue("$json", Serialize(alert));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task SaveBarsAsync(
        IReadOnlyCollection<Bar> bars,
        CancellationToken cancellationToken = default)
    {
        if (bars.Count == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO bars(instrument, timeframe, start_utc, json)
            VALUES($instrument, $timeframe, $start_utc, $json)
            ON CONFLICT(instrument, timeframe, start_utc)
            DO UPDATE SET json = excluded.json
            """;
        var instrumentParameter = command.Parameters.Add("$instrument", SqliteType.Text);
        var timeframeParameter = command.Parameters.Add("$timeframe", SqliteType.Integer);
        var startParameter = command.Parameters.Add("$start_utc", SqliteType.Text);
        var jsonParameter = command.Parameters.Add("$json", SqliteType.Text);

        foreach (var bar in bars.Where(BarIntegrity.HasValidPrices))
        {
            instrumentParameter.Value = bar.Instrument.ToString();
            timeframeParameter.Value = (int)bar.Timeframe;
            startParameter.Value = bar.StartTime.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
            jsonParameter.Value = Serialize(bar);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Bar>> GetCachedBarsAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT json FROM (
                SELECT start_utc, json FROM bars
                WHERE instrument = $instrument AND timeframe = $timeframe
                ORDER BY start_utc DESC
                LIMIT $limit
            )
            ORDER BY start_utc
            """;
        command.Parameters.AddWithValue("$instrument", instrument.ToString());
        command.Parameters.AddWithValue("$timeframe", (int)timeframe);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5_000));
        var result = new List<Bar>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var bar = Deserialize<Bar>(reader.GetString(0));
            if (BarIntegrity.HasValidPrices(bar))
            {
                result.Add(bar);
            }
        }

        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureAlertDeduplicationSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var hasColumn = false;
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(alert_events)";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(reader.GetString(1), "dedup_key", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE alert_events ADD COLUMN dedup_key TEXT";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var migrate = connection.CreateCommand();
        migrate.CommandText =
            """
            UPDATE alert_events SET dedup_key = id
            WHERE dedup_key IS NULL OR dedup_key = '';
            CREATE UNIQUE INDEX IF NOT EXISTS ux_alert_events_dedup
                ON alert_events(dedup_key);
            """;
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> ReadSingleJsonAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task<IReadOnlyList<T>> ReadManyAsync<T>(
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Deserialize<T>(reader.GetString(0)));
        }

        return result;
    }

    private async Task ExecuteJsonAsync(
        string sql,
        IReadOnlyCollection<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException($"无法反序列化 {typeof(T).Name}");
}
