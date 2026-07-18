using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using StockWatchdog.App.Services;
using StockWatchdog.App.ViewModels;
using StockWatchdog.App.Views;
using StockWatchdog.Application.Alerts;
using StockWatchdog.Application.Analysis;
using StockWatchdog.Application.Services;
using StockWatchdog.Infrastructure.MarketData;
using StockWatchdog.Infrastructure.Persistence;

namespace StockWatchdog.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private HttpClient? _primaryHttpClient;
    private HttpClient? _fallbackHttpClient;
    private MarketMonitorService? _monitor;
    private GlobalHotkeyService? _hotkey;
    private TrayIconService? _tray;
    private WindowCoordinator? _windows;
    private LocalLog? _log;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        try
        {
            _singleInstance = new Mutex(true, "Local\\StockWatchdog.Singleton", out var created);
            if (!created)
            {
                System.Windows.MessageBox.Show(
                    "StockWatchdog 已在运行，请通过托盘或老板键恢复窗口。",
                    "StockWatchdog",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            var localRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StockWatchdog");
            Directory.CreateDirectory(localRoot);
            _log = new LocalLog(Path.Combine(localRoot, "logs"));

            var repository = new SqliteAppRepository(Path.Combine(localRoot, "stock-watchdog.db"));
            await repository.InitializeAsync().ConfigureAwait(true);
            var initialSettings = await repository.GetSettingsAsync().ConfigureAwait(true);
            var themes = new ThemeManager(Path.Combine(localRoot, "themes"));
            themes.Apply(initialSettings.ThemeId);

            _primaryHttpClient = new HttpClient();
            _fallbackHttpClient = new HttpClient();
            var primary = new EastMoneyMarketDataProvider(_primaryHttpClient);
            var fallback = new TencentSnapshotProvider(_fallbackHttpClient);
            var provider = new ResilientMarketDataProvider(primary, fallback);
            var analysisEngine = new TechnicalAnalysisEngine();
            _monitor = new MarketMonitorService(
                provider,
                repository,
                analysisEngine,
                new SystemClock(),
                new AlertEvaluator());
            var viewModel = new MainViewModel(repository, _monitor);
            var compact = new CompactWindow();
            var detail = new DetailWindow();
            var privacy = new PrivacyController();
            _windows = new WindowCoordinator(
                compact,
                detail,
                viewModel,
                repository,
                _monitor,
                themes,
                privacy);
            MainWindow = compact;

            _hotkey = new GlobalHotkeyService(compact);
            _windows.AttachHotkey(_hotkey);
            var registration = _windows.RegisterHotkey(initialSettings.EffectiveBossHotkey);
            if (!registration.Success)
            {
                _log.Write("WARN", $"老板键注册失败：{registration.Error}");
            }

            _tray = new TrayIconService();
            _tray.ShowHideRequested += (_, _) => Dispatcher.Invoke(_windows.ToggleBossMode);
            _tray.SettingsRequested += (_, _) => Dispatcher.Invoke(
                () => viewModel.OpenSettingsCommand.Execute(null));
            _tray.ExitRequested += async (_, _) => await ExitAsync().ConfigureAwait(true);

            _monitor.AlertRaised += (_, args) => Dispatcher.BeginInvoke(
                () => _windows.HandleAlert(args.Alert));
            _monitor.QuotesUpdated += (_, args) =>
            {
                var observedAt = DateTimeOffset.Now;
                var ages = args.Quotes
                    .Select(x => Math.Max(0, (observedAt - x.SourceTime).TotalMilliseconds))
                    .Order()
                    .ToArray();
                var p95Index = ages.Length == 0
                    ? 0
                    : Math.Clamp((int)Math.Ceiling(ages.Length * 0.95) - 1, 0, ages.Length - 1);
                var p95Age = ages.Length == 0 ? 0 : ages[p95Index];
                var healthy = args.Quotes.Count(
                    x => x.Quality == StockWatchdog.Domain.Market.MarketDataQuality.Healthy);
                var divergent = args.Quotes.Count(
                    x => x.Quality == StockWatchdog.Domain.Market.MarketDataQuality.Divergent);
                _log.Write(
                    "HEALTH",
                    $"quotes={args.Quotes.Count} healthy={healthy} divergent={divergent} p95AgeMs={p95Age:0}");
            };
            _monitor.StatusChanged += (_, args) =>
            {
                if (args.IsError)
                {
                    _log.Write("WARN", args.Message);
                }
            };

            SystemEvents.SessionSwitch += OnSessionSwitch;
            DispatcherUnhandledException += (_, args) =>
            {
                _log.Write("ERROR", args.Exception.Message);
                if (_windows?.Privacy.IsHidden != true)
                {
                    System.Windows.MessageBox.Show(
                        $"发生未处理错误：{args.Exception.Message}",
                        "StockWatchdog",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                args.Handled = true;
            };

            await _monitor.StartAsync().ConfigureAwait(true);
            await viewModel.InitializeAsync().ConfigureAwait(true);
            _windows.ApplySettings(_monitor.Settings);
            _windows.RestoreLayout(_monitor.Settings);
            var hidden = _monitor.Settings.StartHidden
                         || eventArgs.Args.Any(
                             x => x.Equals("--hidden", StringComparison.OrdinalIgnoreCase));
            _windows.ShowInitial(hidden);
            _log.Write("INFO", "应用启动完成");
        }
        catch (Exception exception)
        {
            _log?.Write("FATAL", exception.Message);
            System.Windows.MessageBox.Show(
                $"StockWatchdog 启动失败：{exception.Message}",
                "StockWatchdog",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _tray?.Dispose();
        _hotkey?.Dispose();
        _primaryHttpClient?.Dispose();
        _fallbackHttpClient?.Dispose();
        if (_singleInstance is not null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _singleInstance.Dispose();
        }

        base.OnExit(eventArgs);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        if (eventArgs.Reason is SessionSwitchReason.SessionLock
            or SessionSwitchReason.RemoteDisconnect)
        {
            Dispatcher.BeginInvoke(() => _windows?.HideAll());
        }
    }

    private async Task ExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        try
        {
            if (_windows is not null)
            {
                await _windows.SaveLayoutAsync().ConfigureAwait(true);
            }

            if (_monitor is not null)
            {
                await _monitor.DisposeAsync().ConfigureAwait(true);
                _monitor = null;
            }

            _windows?.PrepareForExit();
            _log?.Write("INFO", "应用正常退出");
        }
        catch (Exception exception)
        {
            _log?.Write("ERROR", $"退出清理失败：{exception.Message}");
        }
        finally
        {
            Shutdown();
        }
    }
}
