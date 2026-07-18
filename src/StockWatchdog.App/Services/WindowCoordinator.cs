using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using StockWatchdog.App.ViewModels;
using StockWatchdog.App.Views;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Application.Services;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.App.Services;

public sealed class WindowCoordinator
{
    private readonly CompactWindow _compact;
    private readonly DetailWindow _detail;
    private readonly MainViewModel _viewModel;
    private readonly IAppRepository _repository;
    private readonly MarketMonitorService _monitor;
    private readonly ThemeManager _themes;
    private readonly PrivacyController _privacy;
    private readonly AlertSoundService _alertSound = new();
    private readonly List<AlertToastWindow> _toasts = [];
    private readonly SemaphoreSlim _markerSync = new(1, 1);
    private GlobalHotkeyService? _hotkey;
    private AppSettings _settings = new();
    private (InstrumentId Instrument, Timeframe Timeframe)? _loadedMarkerContext;

    public WindowCoordinator(
        CompactWindow compact,
        DetailWindow detail,
        MainViewModel viewModel,
        IAppRepository repository,
        MarketMonitorService monitor,
        ThemeManager themes,
        PrivacyController privacy)
    {
        _compact = compact;
        _detail = detail;
        _viewModel = viewModel;
        _repository = repository;
        _monitor = monitor;
        _themes = themes;
        _privacy = privacy;

        _compact.DataContext = viewModel;
        _detail.DataContext = viewModel;
        _privacy.Register(_compact);
        _privacy.Register(_detail);

        _viewModel.DetailRequested += (_, _) => ShowDetail();
        _viewModel.DetailCloseRequested += (_, _) => _detail.Hide();
        _viewModel.AnalysisChanged += OnAnalysisContextChanged;
        _viewModel.SettingsRequested += (_, _) => ShowSettings();
        _viewModel.AlertRulesRequested += (_, _) => ShowAlertRules();
        _detail.TradeMarkersChanged += OnTradeMarkersChanged;
        _compact.HideRequested += (_, _) => HideAll();
    }

    public CompactWindow CompactWindow => _compact;

    public PrivacyController Privacy => _privacy;

    public void AttachHotkey(GlobalHotkeyService hotkey)
    {
        _hotkey = hotkey;
        _hotkey.Pressed += (_, _) => ToggleBossMode();
    }

    public (bool Success, string? Error) RegisterHotkey(HotkeySettings settings)
    {
        if (_hotkey is null)
        {
            return (false, "快捷键服务尚未启动");
        }

        var success = _hotkey.Register(settings, out var error);
        return (success, error);
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings.Normalize();
        var theme = _themes.Apply(_settings.ThemeId);
        _compact.ApplySettings(_settings, theme);
        _detail.Topmost = _settings.AlwaysOnTop;
        _detail.Opacity = theme.Opacity;
        _detail.Title = "技术形态分析";
        _compact.Title = theme.WindowTitle;
    }

    public void RestoreLayout(AppSettings settings)
    {
        if (double.IsFinite(settings.CompactLeft) && double.IsFinite(settings.CompactTop))
        {
            _compact.Left = settings.CompactLeft;
            _compact.Top = settings.CompactTop;
        }
        else
        {
            _compact.WindowStartupLocation = WindowStartupLocation.Manual;
            var workArea = SystemParameters.WorkArea;
            _compact.Left = workArea.Right - settings.CompactWidth - 20;
            _compact.Top = workArea.Top + 50;
        }

        _compact.Width = settings.CompactWidth;
        _compact.Height = settings.CompactHeight;
        ConstrainToWorkArea(_compact);
    }

    public void ShowInitial(bool hidden)
    {
        _compact.Show();
        if (hidden)
        {
            _privacy.HideAll();
        }
    }

    public void ToggleBossMode()
    {
        if (_privacy.IsHidden)
        {
            var count = _privacy.Restore();
            ConstrainToWorkArea(_compact);
            if (_detail.IsVisible)
            {
                PositionDetail();
            }

            if (count > 0)
            {
                ShowSummaryAlert(count);
            }
        }
        else
        {
            HideAll();
        }
    }

    public void HideAll()
    {
        _alertSound.Stop();
        foreach (var toast in _toasts.ToArray())
        {
            toast.Close();
        }

        _privacy.HideAll();
    }

    public void HandleAlert(AlertEvent alert)
    {
        if (_privacy.IsHidden)
        {
            _privacy.NoteHiddenAlert();
            return;
        }

        var toast = new AlertToastWindow(alert, TimeSpan.FromSeconds(7));
        _toasts.Add(toast);
        _privacy.Register(toast);
        toast.Closed += (_, _) =>
        {
            _toasts.Remove(toast);
            _privacy.Unregister(toast);
            RepositionToasts();
        };
        toast.Show();
        RepositionToasts();
        if (_settings.SoundEnabled)
        {
            _alertSound.Play();
        }
    }

    public async Task SaveLayoutAsync()
    {
        await _markerSync.WaitAsync().ConfigureAwait(true);
        _markerSync.Release();

        var settings = (await _repository.GetSettingsAsync().ConfigureAwait(true)) with
        {
            CompactLeft = _compact.Left,
            CompactTop = _compact.Top,
            CompactWidth = _compact.ActualWidth > 0 ? _compact.ActualWidth : _compact.Width,
            CompactHeight = _compact.ActualHeight > 0 ? _compact.ActualHeight : _compact.Height,
            SelectedInstrument = _viewModel.SelectedRow?.Instrument.ToString()
        };
        await _repository.SaveSettingsAsync(settings).ConfigureAwait(true);
    }

    public void PrepareForExit()
    {
        _alertSound.Stop();
        foreach (var toast in _toasts.ToArray())
        {
            toast.Close();
        }

        _detail.Close();
        _compact.AllowClose();
        _compact.Close();
    }

    private void ShowDetail()
    {
        if (_privacy.IsHidden || !_compact.IsVisible || _viewModel.SelectedRow is null)
        {
            return;
        }

        _ = LoadTradeMarkersAsync(force: false);
        PositionDetail();
        if (!_detail.IsVisible)
        {
            _detail.Show();
        }

        _detail.Activate();
    }

    private void OnAnalysisContextChanged(object? sender, EventArgs eventArgs) =>
        _ = LoadTradeMarkersAsync(force: false);

    private void OnTradeMarkersChanged(object? sender, EventArgs eventArgs)
    {
        if (!TryGetMarkerContext(out var instrument, out var timeframe))
        {
            return;
        }

        var markers = _detail.TradeMarkers
            .Where(marker =>
                marker.Instrument == instrument
                && marker.Timeframe == timeframe)
            .ToArray();
        _ = PersistTradeMarkersAsync(instrument, timeframe, markers);
    }

    private async Task LoadTradeMarkersAsync(bool force)
    {
        if (!TryGetMarkerContext(out var instrument, out var timeframe))
        {
            return;
        }

        var context = (instrument, timeframe);
        if (!force && _loadedMarkerContext == context)
        {
            return;
        }

        await _markerSync.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!force && _loadedMarkerContext == context)
            {
                return;
            }

            var markers = await _repository
                .GetChartTradeMarkersAsync(instrument, timeframe)
                .ConfigureAwait(true);
            if (TryGetMarkerContext(out var currentInstrument, out var currentTimeframe)
                && currentInstrument == instrument
                && currentTimeframe == timeframe)
            {
                _detail.ReplaceTradeMarkers(markers);
                _loadedMarkerContext = context;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to load chart markers: {exception.Message}");
        }
        finally
        {
            _markerSync.Release();
        }
    }

    private async Task PersistTradeMarkersAsync(
        InstrumentId instrument,
        Timeframe timeframe,
        IReadOnlyList<ChartTradeMarker> markers)
    {
        await _markerSync.WaitAsync().ConfigureAwait(true);
        try
        {
            var existing = await _repository
                .GetChartTradeMarkersAsync(instrument, timeframe)
                .ConfigureAwait(true);
            var desiredIds = markers.Select(marker => marker.Id).ToHashSet();
            foreach (var removed in existing.Where(marker => !desiredIds.Contains(marker.Id)))
            {
                await _repository
                    .DeleteChartTradeMarkerAsync(removed.Id)
                    .ConfigureAwait(true);
            }

            foreach (var marker in markers)
            {
                await _repository
                    .UpsertChartTradeMarkerAsync(marker)
                    .ConfigureAwait(true);
            }

            _loadedMarkerContext = (instrument, timeframe);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Failed to save chart markers: {exception.Message}");
        }
        finally
        {
            _markerSync.Release();
        }
    }

    private bool TryGetMarkerContext(
        out InstrumentId instrument,
        out Timeframe timeframe)
    {
        if (_viewModel.SelectedRow is not { } selected)
        {
            instrument = default;
            timeframe = default;
            return false;
        }

        instrument = selected.Instrument;
        timeframe = _viewModel.SelectedTimeframe.Value;
        return true;
    }

    private void PositionDetail()
    {
        var area = GetWorkAreaInDips(_compact);
        var right = _compact.Left + _compact.ActualWidth + 8;
        var left = _compact.Left - _detail.Width - 8;
        _detail.Left = right + _detail.Width <= area.Right ? right : Math.Max(area.Left, left);
        _detail.Top = Math.Clamp(_compact.Top, area.Top, Math.Max(area.Top, area.Bottom - _detail.Height));
    }

    private void ShowSettings()
    {
        if (_privacy.IsHidden)
        {
            return;
        }

        var window = new SettingsWindow(
            _repository,
            _monitor,
            _themes,
            RegisterHotkey)
        {
            Owner = _compact
        };
        _privacy.Register(window);
        window.SettingsSaved += (_, settings) =>
        {
            ApplySettings(settings);
            try
            {
                StartupManager.Apply(settings.StartWithWindows);
            }
            catch (Exception)
            {
            }
        };
        window.Closed += (_, _) => _privacy.Unregister(window);
        window.ShowDialog();
    }

    private void ShowAlertRules()
    {
        if (_privacy.IsHidden || _viewModel.SelectedRow is not { } selected)
        {
            return;
        }

        var window = new AlertRulesWindow(
            selected.Instrument,
            selected.Name,
            _repository,
            _monitor)
        {
            Owner = _compact
        };
        _privacy.Register(window);
        window.Closed += (_, _) => _privacy.Unregister(window);
        window.ShowDialog();
    }

    private void ShowSummaryAlert(int count)
    {
        var now = DateTimeOffset.Now;
        HandleAlert(new AlertEvent(
            Guid.NewGuid(),
            null,
            default,
            AlertRuleType.Pattern,
            AlertPriority.Information,
            "隐藏期间提醒汇总",
            $"隐藏期间共触发 {count} 条提醒，请打开详情逐项核对。",
            now,
            now + TimeSpan.FromMinutes(5),
            $"privacy-summary:{now.UtcTicks}",
            null));
    }

    private void RepositionToasts()
    {
        var area = GetWorkAreaInDips(_compact);
        var active = _toasts.Where(x => x.IsVisible).TakeLast(3).ToArray();
        for (var index = 0; index < active.Length; index++)
        {
            active[index].Left = area.Right - active[index].Width - 16;
            active[index].Top = area.Bottom - (index + 1) * (active[index].Height + 10) - 12;
        }
    }

    private static void ConstrainToWorkArea(Window window)
    {
        var area = GetWorkAreaInDips(window);
        window.Left = Math.Clamp(window.Left, area.Left, Math.Max(area.Left, area.Right - window.Width));
        window.Top = Math.Clamp(window.Top, area.Top, Math.Max(area.Top, area.Bottom - window.Height));
    }

    private static Rect GetWorkAreaInDips(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var pixels = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
        {
            return new Rect(pixels.Left, pixels.Top, pixels.Width, pixels.Height);
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new System.Windows.Point(pixels.Left, pixels.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(pixels.Right, pixels.Bottom));
        return new Rect(topLeft, bottomRight);
    }
}
