using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Market;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.Application.Configuration;

public sealed record PortableConfigurationBundle(
    int FormatVersion,
    string Product,
    DateTimeOffset ExportedAt,
    AppSettings Settings,
    IReadOnlyList<WatchItem> WatchItems,
    IReadOnlyList<AlertRule> AlertRules,
    IReadOnlyList<ThemeDefinition> CustomThemes);

public static class PortableConfigurationCodec
{
    public const string Prefix = "SWCFG1";
    public const int CurrentFormatVersion = 1;
    private const int MaximumEncodedLength = 2_000_000;
    private const int MaximumJsonLength = 4_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Encode(PortableConfigurationBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var normalized = ValidateAndNormalize(bundle);
        var json = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        if (json.Length > MaximumJsonLength)
        {
            throw new InvalidDataException("配置内容过大，无法导出");
        }

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(json);
        }

        var checksum = Convert.ToHexString(SHA256.HashData(json))[..16];
        var payload = Convert.ToBase64String(compressed.ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return $"{Prefix}.{checksum}.{payload}";
    }

    public static PortableConfigurationBundle Decode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("配置文本为空");
        }

        var normalizedText = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
        if (normalizedText.Length > MaximumEncodedLength)
        {
            throw new InvalidDataException("配置文本过大");
        }

        var parts = normalizedText.Split('.', 3, StringSplitOptions.None);
        if (parts.Length != 3 || !parts[0].Equals(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("不是有效的 StockWatchdog 配置文本");
        }

        if (parts[1].Length != 16 || parts[1].Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("配置文本校验码损坏");
        }

        byte[] compressed;
        try
        {
            var base64 = parts[2].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            compressed = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("配置文本编码损坏", exception);
        }

        byte[] json;
        try
        {
            using var source = new MemoryStream(compressed);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16_384];
            while (true)
            {
                var read = gzip.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                if (output.Length > MaximumJsonLength)
                {
                    throw new InvalidDataException("配置解压后过大");
                }
            }

            json = output.ToArray();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or NotSupportedException)
        {
            throw new InvalidDataException("配置压缩内容损坏", exception);
        }

        var expectedChecksum = Convert.ToHexString(SHA256.HashData(json))[..16];
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedChecksum),
                Encoding.ASCII.GetBytes(parts[1].ToUpperInvariant())))
        {
            throw new InvalidDataException("配置文本校验失败，内容可能不完整");
        }

        PortableConfigurationBundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<PortableConfigurationBundle>(json, JsonOptions)
                     ?? throw new InvalidDataException("配置内容为空");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("配置 JSON 内容损坏", exception);
        }

        return ValidateAndNormalize(bundle);
    }

    private static PortableConfigurationBundle ValidateAndNormalize(
        PortableConfigurationBundle bundle)
    {
        if (bundle.Settings is null
            || bundle.WatchItems is null
            || bundle.AlertRules is null
            || bundle.CustomThemes is null
            || bundle.FormatVersion != CurrentFormatVersion
            || !string.Equals(bundle.Product, "StockWatchdog", StringComparison.Ordinal))
        {
            throw new InvalidDataException("配置版本不受支持");
        }

        if (bundle.WatchItems.Count > 50)
        {
            throw new InvalidDataException("自选标的超过 50 个");
        }

        var watchItems = bundle.WatchItems
            .OrderBy(item => item.SortOrder)
            .Select((item, index) =>
            {
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    throw new InvalidDataException("自选标的名称无效");
                }

                return item with
                {
                    SortOrder = index,
                    Name = Limit(item.Name.Trim(), 80),
                    CustomName = string.IsNullOrWhiteSpace(item.CustomName)
                        ? null
                        : Limit(item.CustomName.Trim(), 24)
                };
            })
            .ToArray();
        if (watchItems.Select(item => item.Instrument).Distinct().Count() != watchItems.Length
            || watchItems.Any(item => !IsValidInstrument(item.Instrument)))
        {
            throw new InvalidDataException("自选标的包含重复或无效代码");
        }

        if (bundle.AlertRules.Count > 1_000
            || bundle.AlertRules.Select(rule => rule.Id).Distinct().Count()
            != bundle.AlertRules.Count)
        {
            throw new InvalidDataException("提醒规则数量过多或存在重复 ID");
        }

        var instruments = watchItems.Select(item => item.Instrument).ToHashSet();
        var rules = bundle.AlertRules.Select(rule =>
        {
            if (!instruments.Contains(rule.Instrument)
                || string.IsNullOrWhiteSpace(rule.Name)
                || rule.MaxTriggersPerDay is < 1 or > 100
                || rule.Cooldown <= TimeSpan.Zero
                || rule.Cooldown > TimeSpan.FromDays(7)
                || rule.ValidFor <= TimeSpan.Zero
                || rule.ValidFor > TimeSpan.FromDays(7)
                || !Enum.IsDefined(rule.Type)
                || !Enum.IsDefined(rule.Priority)
                || rule.Type is AlertRuleType.TScoreBuy or AlertRuleType.TScoreSell
                || rule.Type == AlertRuleType.Pattern && string.IsNullOrWhiteSpace(rule.PatternId)
                || rule.Type != AlertRuleType.Pattern && rule.Threshold is null)
            {
                throw new InvalidDataException("提醒规则包含无效内容");
            }

            return rule with { Name = Limit(rule.Name.Trim(), 80) };
        }).ToArray();

        if (bundle.CustomThemes.Count > 50
            || bundle.CustomThemes.Select(theme => theme.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != bundle.CustomThemes.Count)
        {
            throw new InvalidDataException("自定义主题数量过多或存在重复 ID");
        }

        foreach (var theme in bundle.CustomThemes)
        {
            if (string.IsNullOrWhiteSpace(theme.Id)
                || string.IsNullOrWhiteSpace(theme.Name)
                || theme.Id.Length > 64
                || theme.Name.Length > 80)
            {
                throw new InvalidDataException("自定义主题名称或 ID 无效");
            }

            if (theme.Id.Any(character =>
                    !char.IsLetterOrDigit(character) && character != '-' && character != '_'))
            {
                throw new InvalidDataException("自定义主题 ID 只能包含字母、数字、- 和 _");
            }
        }

        return bundle with
        {
            Settings = bundle.Settings.Normalize(),
            WatchItems = watchItems,
            AlertRules = rules,
            CustomThemes = bundle.CustomThemes.ToArray()
        };
    }

    private static bool IsValidInstrument(InstrumentId instrument) =>
        InstrumentId.TryParse(instrument.ToString(), out var parsed)
        && parsed == instrument
        && Enum.IsDefined(instrument.Exchange)
        && Enum.IsDefined(instrument.AssetType);

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
