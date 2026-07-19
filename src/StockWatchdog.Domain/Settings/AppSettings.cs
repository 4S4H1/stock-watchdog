namespace StockWatchdog.Domain.Settings;

public sealed record HotkeySettings(
    int Modifiers = 0x0001 | 0x0002 | 0x4000,
    int VirtualKey = 0x48,
    string DisplayText = "Ctrl+Alt+H");

public enum CompactDisplayMode
{
    Rich,
    Minimal
}

public sealed record AppSettings(
    int RefreshSeconds = 5,
    int MaximumWatchItems = 50,
    int MaximumAnalysisItems = 30,
    string ThemeId = "light",
    bool AlwaysOnTop = true,
    bool SoundEnabled = true,
    bool StartHidden = false,
    bool StartWithWindows = false,
    bool ShowCodeColumn = true,
    bool ShowSignalColumn = true,
    bool ShowUpdateTimeColumn = true,
    HotkeySettings? BossHotkey = null,
    double CompactLeft = double.NaN,
    double CompactTop = double.NaN,
    double CompactWidth = 680,
    double CompactHeight = 320,
    string? SelectedInstrument = null,
    CompactDisplayMode CompactMode = CompactDisplayMode.Rich,
    bool ShowNameColumn = true,
    bool ShowPriceColumn = true,
    bool ShowChangeColumn = true,
    bool ShowSparklineColumn = true,
    bool TScoreEnabled = true,
    int TScoreAlertThreshold = 75,
    int TScoreCooldownMinutes = 10,
    int SettingsSchemaVersion = 2)
{
    public HotkeySettings EffectiveBossHotkey => BossHotkey ?? new HotkeySettings();

    public AppSettings Normalize() => this with
    {
        RefreshSeconds = Math.Clamp(RefreshSeconds, 3, 60),
        MaximumWatchItems = Math.Clamp(MaximumWatchItems, 1, 50),
        MaximumAnalysisItems = Math.Clamp(MaximumAnalysisItems, 1, 30),
        CompactWidth = Math.Clamp(CompactWidth, 320, 900),
        CompactHeight = Math.Clamp(CompactHeight, 120, 900),
        TScoreEnabled = SettingsSchemaVersion < 2 || TScoreEnabled,
        TScoreAlertThreshold = SettingsSchemaVersion < 2
            ? 75
            : Math.Clamp(TScoreAlertThreshold, 60, 95),
        TScoreCooldownMinutes = SettingsSchemaVersion < 2
            ? 10
            : Math.Clamp(TScoreCooldownMinutes, 1, 240),
        SettingsSchemaVersion = 2,
        CompactMode = Enum.IsDefined(CompactMode)
            ? CompactMode
            : CompactDisplayMode.Rich
    };
}

public sealed record ThemeDefinition(
    string Id,
    string Name,
    string Background,
    string Surface,
    string Foreground,
    string MutedForeground,
    string GridLine,
    string Positive,
    string Negative,
    string Warning,
    string Accent,
    string Selection,
    string FontFamily,
    double FontSize,
    double RowHeight,
    double Opacity,
    string WindowTitle,
    bool SquareCells)
{
    public static IReadOnlyList<ThemeDefinition> BuiltIns { get; } =
    [
        new(
            "light", "浅色", "#F5F7FA", "#FFFFFF", "#1B2430", "#64748B", "#E2E8F0",
            "#D93448", "#14966A", "#C88700", "#2563EB", "#E7F0FF",
            "Segoe UI", 12, 30, 0.97, "行情观察", false),
        new(
            "dark", "深色", "#111827", "#18212F", "#E5E7EB", "#94A3B8", "#334155",
            "#FF6B7A", "#31C48D", "#FBBF24", "#60A5FA", "#24324A",
            "Segoe UI", 12, 30, 0.97, "行情观察", false),
        new(
            "spreadsheet", "电子表格", "#FFFFFF", "#FFFFFF", "#202020", "#666666", "#D0D0D0",
            "#C00000", "#008000", "#9C6500", "#217346", "#E2F0D9",
            "Segoe UI", 11, 24, 1.0, "项目跟踪表", true)
    ];
}
