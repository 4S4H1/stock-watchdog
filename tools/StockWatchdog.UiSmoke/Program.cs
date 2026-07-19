using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StockWatchdog.App.Services;
using StockWatchdog.App.ViewModels;
using StockWatchdog.App.Views;
using StockWatchdog.Application.Abstractions;
using StockWatchdog.Application.Alerts;
using StockWatchdog.Application.Analysis;
using StockWatchdog.Application.Services;
using StockWatchdog.Domain.Alerts;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;
using StockWatchdog.Domain.Settings;
using StockWatchdog.Infrastructure.Persistence;

namespace StockWatchdog.UiSmoke;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var outputDirectory = Path.GetFullPath(
            args.FirstOrDefault()
            ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-smoke"));
        Directory.CreateDirectory(outputDirectory);

        var application = new StockWatchdog.App.App();
        application.InitializeComponent();

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"stock-watchdog-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        var repository = new SqliteAppRepository(Path.Combine(temporaryRoot, "ui-smoke.db"));
        repository.InitializeAsync().GetAwaiter().GetResult();
        var themes = new ThemeManager(Path.Combine(temporaryRoot, "themes"));
        var monitor = new MarketMonitorService(
            new EmptyProvider(),
            repository,
            new TechnicalAnalysisEngine(),
            new SystemClock(),
            new AlertEvaluator());
        var viewModel = BuildViewModel(repository, monitor);

        var compact = new CompactWindow
        {
            DataContext = viewModel,
            Left = -20_000,
            Top = -20_000
        };
        themes.Apply("light");
        compact.ApplySettings(new AppSettings(), ThemeDefinition.BuiltIns[0]);
        RenderWindow(compact, Path.Combine(outputDirectory, "compact-rich.png"));
        themes.Apply("dark");
        compact.ApplySettings(
            new AppSettings(ThemeId: "dark"),
            ThemeDefinition.BuiltIns[1]);
        RenderWindow(compact, Path.Combine(outputDirectory, "compact-dark.png"));
        themes.Apply("spreadsheet");
        compact.ApplySettings(
            new AppSettings(CompactMode: CompactDisplayMode.Minimal),
            ThemeDefinition.BuiltIns[2]);
        RenderWindow(compact, Path.Combine(outputDirectory, "compact-minimal.png"));
        compact.AllowClose();
        compact.Close();

        var detail = new DetailWindow
        {
            DataContext = viewModel,
            Left = -20_000,
            Top = -20_000
        };
        themes.Apply("dark");
        var snapshot = viewModel.SelectedAnalysis!;
        var anchor = snapshot.Bars[^22];
        var lossAnchor = snapshot.Bars[^12];
        detail.ReplaceTradeMarkers(
        [
            new(
                Guid.NewGuid(),
                snapshot.Instrument,
                snapshot.Timeframe,
                StockWatchdog.Domain.Analysis.ChartTradeSide.Buy,
                anchor.StartTime,
                anchor.Close - 0.015m,
                snapshot.CalculatedAt),
            new(
                Guid.NewGuid(),
                snapshot.Instrument,
                snapshot.Timeframe,
                StockWatchdog.Domain.Analysis.ChartTradeSide.Sell,
                lossAnchor.StartTime,
                snapshot.Bars[^1].Close + 0.025m,
                snapshot.CalculatedAt.AddSeconds(1))
        ]);
        RenderWindow(detail, Path.Combine(outputDirectory, "detail-dark.png"));
        detail.Close();

        repository.SaveSettingsAsync(new AppSettings(ThemeId: "dark"))
            .GetAwaiter()
            .GetResult();
        var settings = new SettingsWindow(
            repository,
            monitor,
            themes,
            _ => (true, null))
        {
            Left = -20_000,
            Top = -20_000
        };
        RenderWindow(settings, Path.Combine(outputDirectory, "settings-dark.png"));
        settings.Close();

        var alerts = new AlertRulesWindow(
            snapshot.Instrument,
            "沪深300ETF",
            repository,
            monitor)
        {
            Left = -20_000,
            Top = -20_000
        };
        RenderWindow(alerts, Path.Combine(outputDirectory, "alerts-dark.png"));
        alerts.Close();

        var portableConfiguration = new PortableConfigurationWindow(
            PortableConfigurationWindowMode.Export,
            $"SWCFG1.0123456789ABCDEF.{new string('A', 420)}")
        {
            Left = -20_000,
            Top = -20_000
        };
        RenderWindow(
            portableConfiguration,
            Path.Combine(outputDirectory, "portable-config-dark.png"));
        portableConfiguration.Close();

        var toastNow = DateTimeOffset.Now;
        var toast = new AlertToastWindow(
            new AlertEvent(
                Guid.NewGuid(),
                null,
                snapshot.Instrument,
                AlertRuleType.TScoreBuy,
                AlertPriority.High,
                "低吸候选 · 买入条件 82 分",
                "价格低于 VWAP，短时量速回升；卖出条件 24 分。",
                toastNow,
                toastNow.AddMinutes(2),
                "ui-smoke",
                null),
            TimeSpan.FromMinutes(1))
        {
            Left = -20_000,
            Top = -20_000
        };
        RenderWindow(toast, Path.Combine(outputDirectory, "toast-dark.png"));
        toast.Close();
        application.Shutdown();
    }

    private static MainViewModel BuildViewModel(
        IAppRepository repository,
        MarketMonitorService monitor)
    {
        _ = InstrumentId.TryParse("510300", out var instrument);
        var viewModel = new MainViewModel(repository, monitor);
        var row = new WatchRowViewModel(
            new WatchItem(
                instrument,
                "沪深300ETF华泰柏瑞",
                0,
                CustomName: "沪深300ETF"));
        var now = new DateTimeOffset(2026, 7, 17, 10, 15, 0, TimeSpan.FromHours(8));
        row.UpdateQuote(new QuoteSnapshot(
            instrument,
            "沪深300ETF华泰柏瑞",
            4.589m,
            4.753m,
            -0.164m,
            -3.45m,
            123_456_700,
            567_890_000m,
            now,
            now,
            "ui-smoke",
            MarketDataQuality.Healthy));
        viewModel.Rows.Add(row);
        viewModel.SelectedRow = row;

        var bars = Enumerable.Range(0, 45)
            .Select(index =>
            {
                var start = now.AddMinutes(index - 45);
                var close = 4.55m + index * 0.002m
                    + (decimal)Math.Sin(index / 3d) * 0.012m;
                return new Bar(
                    instrument,
                    Timeframe.Minute1,
                    start,
                    start.AddMinutes(1),
                    close - 0.004m,
                    close + 0.010m,
                    close - 0.012m,
                    close,
                    1_000_000 + index * 10_000,
                    close * (1_000_000 + index * 10_000),
                    true,
                    "ui-smoke");
            })
            .ToArray();
        var snapshot = new TechnicalAnalysisEngine().Analyze(
            instrument,
            Timeframe.Minute1,
            bars,
            now);
        row.UpdateSparkline(snapshot);
        row.UpdateAnalysis(snapshot);
        row.UpdateTSignal(new TSignalSnapshot(
            instrument,
            78,
            21,
            TSignalState.BuyCandidate,
            "低吸候选 · 价格低于 VWAP、短时量速回升",
            now,
            now.AddMinutes(2),
            MarketDataQuality.Healthy,
            []));
        typeof(MainViewModel)
            .GetProperty(
                nameof(MainViewModel.SelectedAnalysis),
                BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(viewModel, snapshot);
        return viewModel;
    }

    private static void RenderWindow(Window window, string path)
    {
        window.Show();
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(
            width,
            height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        window.Hide();
    }

    private sealed class EmptyProvider : IMarketDataProvider
    {
        public string Name => "ui-smoke";

        public ProviderCapabilities Capabilities =>
            ProviderCapabilities.Quotes
            | ProviderCapabilities.MinuteBars
            | ProviderCapabilities.DailyBars;

        public Task<ProviderResult<IReadOnlyList<QuoteSnapshot>>> GetQuotesAsync(
            IReadOnlyCollection<InstrumentId> instruments,
            CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<IReadOnlyList<QuoteSnapshot>>.Success(
                [],
                Name,
                DateTimeOffset.Now));

        public Task<ProviderResult<IReadOnlyList<Bar>>> GetBarsAsync(
            InstrumentId instrument,
            Timeframe timeframe,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<IReadOnlyList<Bar>>.Success(
                [],
                Name,
                DateTimeOffset.Now));
    }
}
