using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.App.Services;

public sealed class ThemeManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _themeDirectory;
    private List<ThemeDefinition> _themes = [];

    public ThemeManager(string themeDirectory)
    {
        _themeDirectory = themeDirectory;
        Directory.CreateDirectory(_themeDirectory);
        Reload();
    }

    public IReadOnlyList<ThemeDefinition> Themes => _themes;

    public ThemeDefinition Current { get; private set; } = ThemeDefinition.BuiltIns[0];

    public event EventHandler? ThemeChanged;

    public void Reload()
    {
        var themes = ThemeDefinition.BuiltIns.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_themeDirectory, "*.json"))
        {
            try
            {
                var theme = JsonSerializer.Deserialize<ThemeDefinition>(
                    File.ReadAllText(file),
                    JsonOptions);
                if (theme is not null && Validate(theme, out _))
                {
                    themes[theme.Id] = theme;
                }
            }
            catch (JsonException)
            {
            }
        }

        _themes = themes.Values.OrderBy(x => x.Name).ToList();
    }

    public ThemeDefinition Apply(string? id)
    {
        Current = _themes.FirstOrDefault(x => string.Equals(
                x.Id,
                id,
                StringComparison.OrdinalIgnoreCase))
            ?? ThemeDefinition.BuiltIns[0];

        var resources = System.Windows.Application.Current.Resources;
        resources["AppBackgroundBrush"] = Brush(Current.Background);
        resources["SurfaceBrush"] = Brush(Current.Surface);
        resources["ForegroundBrush"] = Brush(Current.Foreground);
        resources["MutedForegroundBrush"] = Brush(Current.MutedForeground);
        resources["GridLineBrush"] = Brush(Current.GridLine);
        resources["PositiveBrush"] = Brush(Current.Positive);
        resources["NegativeBrush"] = Brush(Current.Negative);
        resources["WarningBrush"] = Brush(Current.Warning);
        resources["AccentBrush"] = Brush(Current.Accent);
        resources["SelectionBrush"] = Brush(Current.Selection);
        resources["AppFontFamily"] = new System.Windows.Media.FontFamily(Current.FontFamily);
        resources["AppFontSize"] = Current.FontSize;
        resources["WatchRowHeight"] = Current.RowHeight;
        resources["SquareCellRadius"] = new CornerRadius(Current.SquareCells ? 0 : 6);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    public ThemeDefinition Import(string sourcePath)
    {
        var theme = JsonSerializer.Deserialize<ThemeDefinition>(
            File.ReadAllText(sourcePath),
            JsonOptions) ?? throw new InvalidDataException("主题文件为空");
        if (!Validate(theme, out var error))
        {
            throw new InvalidDataException(error);
        }

        var safeId = string.Concat(theme.Id.Where(x => char.IsLetterOrDigit(x) || x is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeId))
        {
            throw new InvalidDataException("主题 ID 只能包含字母、数字、- 和 _");
        }

        var normalized = theme with
        {
            Id = safeId,
            FontSize = Math.Clamp(theme.FontSize, 9, 20),
            RowHeight = Math.Clamp(theme.RowHeight, 20, 48),
            Opacity = Math.Clamp(theme.Opacity, 0.5, 1)
        };
        File.WriteAllText(
            Path.Combine(_themeDirectory, $"{safeId}.json"),
            JsonSerializer.Serialize(normalized, JsonOptions));
        Reload();
        return normalized;
    }

    public void Export(ThemeDefinition theme, string targetPath) =>
        File.WriteAllText(targetPath, JsonSerializer.Serialize(theme, JsonOptions));

    private static SolidColorBrush Brush(string value)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static bool Validate(ThemeDefinition theme, out string error)
    {
        if (string.IsNullOrWhiteSpace(theme.Id) || string.IsNullOrWhiteSpace(theme.Name))
        {
            error = "主题必须包含 ID 和名称";
            return false;
        }

        foreach (var color in new[]
                 {
                     theme.Background,
                     theme.Surface,
                     theme.Foreground,
                     theme.MutedForeground,
                     theme.GridLine,
                     theme.Positive,
                     theme.Negative,
                     theme.Warning,
                     theme.Accent,
                     theme.Selection
                 })
        {
            try
            {
                _ = System.Windows.Media.ColorConverter.ConvertFromString(color);
            }
            catch (Exception exception) when (
                exception is FormatException
                    or NotSupportedException
                    or ArgumentException)
            {
                error = $"无效颜色：{color}";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}
