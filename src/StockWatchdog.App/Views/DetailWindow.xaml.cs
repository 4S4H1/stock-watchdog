using System.Windows;
using System.Windows.Input;
using ScottPlot;
using StockWatchdog.App.ViewModels;
using StockWatchdog.Application.Analysis;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;
using MarketBar = StockWatchdog.Domain.Market.Bar;
using PlotColor = ScottPlot.Color;

namespace StockWatchdog.App.Views;

public partial class DetailWindow : Window
{
    private const string ChartFontName = "Microsoft YaHei UI";
    private readonly List<ChartTradeMarker> _tradeMarkers = [];
    private IReadOnlyList<MarketBar> _displayBars = [];
    private bool _defaultTimeframeApplied;

    public DetailWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => AttachAndRender();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                AttachAndRender();
            }
        };
    }

    /// <summary>
    /// Raised after a user adds or clears a chart marker. Persistence code can
    /// read <see cref="TradeMarkers"/> and store the complete in-memory set.
    /// </summary>
    public event EventHandler? TradeMarkersChanged;

    public IReadOnlyList<ChartTradeMarker> TradeMarkers => _tradeMarkers.ToArray();

    public void ApplyTheme() => RenderCurrentChart();

    /// <summary>
    /// Replaces the in-memory markers without raising <see cref="TradeMarkersChanged"/>.
    /// This is the integration point for loading persisted annotations.
    /// </summary>
    public void ReplaceTradeMarkers(IEnumerable<ChartTradeMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        _tradeMarkers.Clear();
        _tradeMarkers.AddRange(
            markers
                .Where(marker => marker.Price > 0)
                .DistinctBy(marker => marker.Id)
                .OrderBy(marker => marker.CreatedAt));
        RenderCurrentChart();
    }

    private void AttachAndRender()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (!_defaultTimeframeApplied)
        {
            var minute1 = viewModel.Timeframes.FirstOrDefault(
                option => option.Value == Timeframe.Minute1);
            if (minute1 is not null)
            {
                viewModel.SelectedTimeframe = minute1;
            }

            _defaultTimeframeApplied = true;
        }

        viewModel.AnalysisChanged -= OnAnalysisChanged;
        viewModel.AnalysisChanged += OnAnalysisChanged;
        RenderChart(viewModel);
    }

    private void OnAnalysisChanged(object? sender, EventArgs eventArgs)
    {
        if (sender is MainViewModel viewModel)
        {
            if (Dispatcher.CheckAccess())
            {
                RenderChart(viewModel);
            }
            else
            {
                Dispatcher.BeginInvoke(() => RenderChart(viewModel));
            }
        }
    }

    private void RenderChart(MainViewModel viewModel)
    {
        var snapshot = viewModel.SelectedAnalysis;
        AnalysisPlot.Plot.Clear();
        ApplyPlotTheme();
        ConfigureLegend();
        if (snapshot is null || snapshot.Bars.Count == 0)
        {
            _displayBars = [];
            AddChartMessage("等待已完成的 K 线数据");
            UpdateMarkerStatus();
            AnalysisPlot.Plot.Axes.AutoScale();
            AnalysisPlot.Refresh();
            return;
        }

        var displayBars = snapshot.Bars
            .Where(BarIntegrity.HasValidPrices)
            .TakeLast(120)
            .ToArray();
        _displayBars = displayBars;
        if (displayBars.Length == 0)
        {
            AddChartMessage("K 线价格数据异常，已暂停绘图");
            UpdateMarkerStatus();
            AnalysisPlot.Plot.Axes.AutoScale();
            AnalysisPlot.Refresh();
            return;
        }

        var duration = snapshot.Timeframe == Timeframe.Day
            ? TimeSpan.FromDays(1)
            : TimeSpan.FromMinutes((int)snapshot.Timeframe);
        var prices = displayBars
            .Select(x => new OHLC(
                (double)x.Open,
                (double)x.High,
                (double)x.Low,
                (double)x.Close,
                x.StartTime.DateTime,
                duration))
            .ToArray();
        var candles = AnalysisPlot.Plot.Add.Candlestick(prices);
        candles.Sequential = false;
        candles.RisingColor = Colors.Red;
        candles.FallingColor = Colors.Green;

        var points = snapshot.Indicators.TakeLast(120).ToArray();
        if (ShowEma5CheckBox.IsChecked == true)
        {
            AddIndicator(points, x => x.Ema5, "EMA5", Colors.DodgerBlue);
        }

        if (ShowEma10CheckBox.IsChecked == true)
        {
            AddIndicator(points, x => x.Ema10, "EMA10", Colors.Orange);
        }

        if (ShowEma20CheckBox.IsChecked == true)
        {
            AddIndicator(points, x => x.Ema20, "EMA20", Colors.Purple);
        }

        if (ShowVwapCheckBox.IsChecked == true)
        {
            AddIndicator(points, x => x.Vwap, "VWAP", Colors.Green);
        }

        if (ShowBollingerCheckBox.IsChecked == true)
        {
            AddIndicator(points, x => x.BollingerUpper, "布林上轨", Colors.Gray);
            AddIndicator(points, x => x.BollingerMiddle, "布林中轨", Colors.DarkGray);
            AddIndicator(points, x => x.BollingerLower, "布林下轨", Colors.Gray);
        }

        var recent = displayBars.TakeLast(20).ToArray();
        if (ShowSupportResistanceCheckBox.IsChecked == true && recent.Length > 0)
        {
            var resistance = AnalysisPlot.Plot.Add.HorizontalLine((double)recent.Max(x => x.High));
            resistance.LegendText = "20期阻力";
            resistance.Color = Colors.Red.WithAlpha(.75);
            resistance.LinePattern = LinePattern.Dashed;
            var support = AnalysisPlot.Plot.Add.HorizontalLine((double)recent.Min(x => x.Low));
            support.LegendText = "20期支撑";
            support.Color = Colors.Green.WithAlpha(.75);
            support.LinePattern = LinePattern.Dashed;
        }

        if (ShowAlertThresholdsCheckBox.IsChecked == true)
        {
            var thresholdIndex = 0;
            foreach (var threshold in viewModel.AlertThresholds)
            {
                var line = AnalysisPlot.Plot.Add.HorizontalLine((double)threshold);
                line.LegendText = thresholdIndex++ == 0 ? "提醒阈值" : string.Empty;
                line.Color = Colors.Orange.WithAlpha(.85);
                line.LinePattern = LinePattern.Dotted;
            }
        }

        AddTradeMarkers(snapshot, displayBars);
        AnalysisPlot.Plot.Axes.DateTimeTicksBottom();
        AnalysisPlot.Plot.ShowLegend();
        AnalysisPlot.Plot.Axes.AutoScale();
        AnalysisPlot.Refresh();
        UpdateMarkerStatus(snapshot);
    }

    private void AddIndicator(
        IReadOnlyList<IndicatorPoint> points,
        Func<IndicatorPoint, decimal?> selector,
        string name,
        PlotColor color)
    {
        var values = points
            .Select(point => (point.Time.DateTime.ToOADate(), Value: selector(point)))
            .Where(x => x.Value is not null)
            .ToArray();
        if (values.Length == 0)
        {
            return;
        }

        var scatter = AnalysisPlot.Plot.Add.Scatter(
            values.Select(x => x.Item1).ToArray(),
            values.Select(x => (double)x.Value!.Value).ToArray());
        scatter.LegendText = name;
        scatter.MarkerSize = 0;
        scatter.LineWidth = 1.5f;
        scatter.Color = color;
    }

    private void AddTradeMarkers(
        AnalysisSnapshot snapshot,
        IReadOnlyList<MarketBar> bars)
    {
        var visibleMarkers = _tradeMarkers
            .Where(marker =>
                marker.Instrument == snapshot.Instrument
                && marker.Timeframe == snapshot.Timeframe)
            .OrderBy(marker => marker.Time)
            .ToArray();
        var buyLegendAdded = false;
        var sellLegendAdded = false;

        foreach (var tradeMarker in visibleMarkers)
        {
            var x = tradeMarker.Time.DateTime.ToOADate();
            var markerShape = tradeMarker.Side == ChartTradeSide.Buy
                ? MarkerShape.FilledTriangleUp
                : MarkerShape.FilledTriangleDown;
            var markerColor = tradeMarker.Side == ChartTradeSide.Buy
                ? Colors.Blue
                : Colors.Orange;
            var marker = AnalysisPlot.Plot.Add.Marker(
                x,
                (double)tradeMarker.Price,
                markerShape);
            marker.MarkerSize = 14;
            marker.MarkerFillColor = markerColor.WithAlpha(.9);
            marker.MarkerLineColor = ResourceColor("ForegroundBrush", Colors.White);
            marker.LineWidth = 1.5f;

            if (tradeMarker.Side == ChartTradeSide.Buy && !buyLegendAdded)
            {
                marker.LegendText = "买入标记";
                buyLegendAdded = true;
            }
            else if (tradeMarker.Side == ChartTradeSide.Sell && !sellLegendAdded)
            {
                marker.LegendText = "卖出标记";
                sellLegendAdded = true;
            }

            var frame = ChartTrendFrameBuilder.Build(tradeMarker, bars);
            if (frame is not null)
            {
                AddTrendFrame(frame);
            }
        }
    }

    private void AddTrendFrame(ChartTrendFrame frame)
    {
        var startX = frame.Marker.Time.DateTime.ToOADate();
        var endX = frame.EndTime.DateTime.ToOADate();
        var bottom = (double)Math.Min(frame.Marker.Price, frame.EndPrice);
        var top = (double)Math.Max(frame.Marker.Price, frame.EndPrice);
        var frameColor = frame.Direction switch
        {
            TrendFrameDirection.RisingProfit => Colors.Red,
            TrendFrameDirection.FallingLoss => Colors.Green,
            _ => Colors.Gray
        };

        if (Math.Abs(endX - startX) > double.Epsilon
            && Math.Abs(top - bottom) > double.Epsilon)
        {
            var rectangle = AnalysisPlot.Plot.Add.Rectangle(
                Math.Min(startX, endX),
                Math.Max(startX, endX),
                bottom,
                top);
            rectangle.FillStyle.Color = frameColor.WithAlpha(.16);
            rectangle.LineStyle.Color = frameColor.WithAlpha(.65);
            rectangle.LineStyle.Width = 1;
        }

        var sideText = frame.Marker.Side == ChartTradeSide.Buy ? "买" : "卖";
        var resultText = frame.Direction switch
        {
            TrendFrameDirection.RisingProfit => "盈利",
            TrendFrameDirection.FallingLoss => "失败",
            _ => "持平"
        };
        var label = AnalysisPlot.Plot.Add.Text(
            $"{sideText} {frame.ChangePercent:+0.00;-0.00;0.00}% {resultText}",
            (startX + endX) / 2,
            (bottom + top) / 2);
        label.LabelFontName = ChartFontName;
        label.LabelFontSize = 9;
        label.LabelFontColor = frameColor;
        label.LabelBackgroundColor = ResourceColor("SurfaceBrush", Colors.White).WithAlpha(.88);
        label.LabelBorderColor = frameColor.WithAlpha(.75);
        label.LabelBorderWidth = 1;
        label.LabelAlignment = Alignment.MiddleCenter;
    }

    private void ConfigureLegend()
    {
        AnalysisPlot.Plot.Legend.FontName = ChartFontName;
        AnalysisPlot.Plot.Legend.SetBestFontOnEachRender = true;
        AnalysisPlot.Plot.Legend.FontSize = 10;
        AnalysisPlot.Plot.Legend.Alignment = Alignment.UpperLeft;
    }

    private void ApplyPlotTheme()
    {
        var surface = ResourceColor("SurfaceBrush", Colors.White);
        var background = ResourceColor("AppBackgroundBrush", Colors.White);
        var foreground = ResourceColor("ForegroundBrush", Colors.Black);
        var grid = ResourceColor("GridLineBrush", Colors.Gray);
        AnalysisPlot.Plot.FigureBackground.Color = background;
        AnalysisPlot.Plot.DataBackground.Color = surface;
        AnalysisPlot.Plot.Axes.Color(foreground);
        AnalysisPlot.Plot.Axes.FrameColor(grid);
        AnalysisPlot.Plot.Grid.MajorLineColor = grid.WithAlpha(.45);
        AnalysisPlot.Plot.Legend.BackgroundColor = surface.WithAlpha(.92);
        AnalysisPlot.Plot.Legend.FontColor = foreground;
        AnalysisPlot.Plot.Legend.OutlineColor = grid;
    }

    private void AddChartMessage(string message)
    {
        var text = AnalysisPlot.Plot.Add.Text(message, 0, 0);
        text.LabelFontName = ChartFontName;
        text.LabelFontSize = 12;
        text.LabelFontColor = ResourceColor("MutedForegroundBrush", Colors.Gray);
        text.LabelAlignment = Alignment.MiddleCenter;
    }

    private static PlotColor ResourceColor(string key, PlotColor fallback)
    {
        if (System.Windows.Application.Current.Resources[key]
            is not System.Windows.Media.SolidColorBrush brush)
        {
            return fallback;
        }

        return new PlotColor(
            brush.Color.R,
            brush.Color.G,
            brush.Color.B,
            brush.Color.A);
    }

    private void OnIndicatorVisibilityChanged(object sender, RoutedEventArgs eventArgs)
    {
        RenderCurrentChart();
    }

    private void OnPlotPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        var side = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? ChartTradeSide.Sell
            : ChartTradeSide.Buy;
        AddTradeMarker(side, eventArgs);
    }

    private void OnPlotPreviewMouseRightButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            return;
        }

        AddTradeMarker(ChartTradeSide.Sell, eventArgs);
    }

    private void OnPlotPreviewMouseDoubleClick(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || viewModel.SelectedAnalysis is not { } snapshot
            || _displayBars.Count == 0)
        {
            return;
        }

        var pixel = AnalysisPlot.GetPlotPixelPosition(eventArgs);
        var coordinates = AnalysisPlot.Plot.GetCoordinates(pixel);
        if (!double.IsFinite(coordinates.X)
            || !double.IsFinite(coordinates.Y)
            || coordinates.Y <= 0
            || coordinates.Y > (double)decimal.MaxValue)
        {
            return;
        }

        DateTime chartTime;
        try
        {
            chartTime = DateTime.FromOADate(coordinates.X);
        }
        catch (ArgumentException)
        {
            return;
        }

        var frames = _tradeMarkers
            .Where(marker =>
                marker.Instrument == snapshot.Instrument
                && marker.Timeframe == snapshot.Timeframe)
            .Select(marker => ChartTrendFrameBuilder.Build(marker, _displayBars))
            .OfType<ChartTrendFrame>()
            .ToArray();
        var hit = ChartTrendFrameHitTester.FindHit(
            frames,
            chartTime,
            (decimal)coordinates.Y);
        if (hit is null)
        {
            MarkerStatusText.Text = "未点中走势框；请双击框体填充区域";
            return;
        }

        var removed = _tradeMarkers.RemoveAll(
            marker => marker.Id == hit.Marker.Id);
        if (removed == 0)
        {
            return;
        }

        eventArgs.Handled = true;
        TradeMarkersChanged?.Invoke(this, EventArgs.Empty);
        RenderChart(viewModel);
        var sideText = hit.Marker.Side == ChartTradeSide.Buy ? "买入" : "卖出";
        MarkerStatusText.Text =
            $"已删除{sideText}框 {hit.Marker.Price:0.###} @ {hit.Marker.Time:HH:mm}（自动同步到本地）";
    }

    private void AddTradeMarker(
        ChartTradeSide side,
        MouseButtonEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || viewModel.SelectedAnalysis is not { } snapshot
            || _displayBars.Count == 0)
        {
            MarkerStatusText.Text = "当前没有可标记的 K 线数据";
            return;
        }

        var pixel = AnalysisPlot.GetPlotPixelPosition(eventArgs);
        var coordinates = AnalysisPlot.Plot.GetCoordinates(pixel);
        if (!double.IsFinite(coordinates.X)
            || !double.IsFinite(coordinates.Y)
            || coordinates.Y <= 0
            || coordinates.Y > (double)decimal.MaxValue)
        {
            MarkerStatusText.Text = "点击位置不在有效价格区域";
            return;
        }

        var nearestBar = _displayBars
            .MinBy(bar => Math.Abs(bar.StartTime.DateTime.ToOADate() - coordinates.X));
        if (nearestBar is null)
        {
            return;
        }

        var priceDecimals = snapshot.Instrument.AssetType == AssetType.Etf ? 3 : 2;
        var price = Math.Round(
            (decimal)coordinates.Y,
            priceDecimals,
            MidpointRounding.AwayFromZero);
        var marker = new ChartTradeMarker(
            Guid.NewGuid(),
            snapshot.Instrument,
            snapshot.Timeframe,
            side,
            nearestBar.StartTime,
            price,
            DateTimeOffset.Now);
        _tradeMarkers.Add(marker);
        eventArgs.Handled = true;
        TradeMarkersChanged?.Invoke(this, EventArgs.Empty);
        RenderChart(viewModel);

        var sideText = side == ChartTradeSide.Buy ? "买入" : "卖出";
        MarkerStatusText.Text =
            $"已标记{sideText} {price:0.###} @ {nearestBar.StartTime:HH:mm}（自动保存到本地）";
    }

    private void OnClearTradeMarkersClick(object sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel
            || viewModel.SelectedAnalysis is not { } snapshot)
        {
            return;
        }

        var removed = _tradeMarkers.RemoveAll(marker =>
            marker.Instrument == snapshot.Instrument
            && marker.Timeframe == snapshot.Timeframe);
        if (removed == 0)
        {
            MarkerStatusText.Text = "当前标的与周期没有标记";
            return;
        }

        TradeMarkersChanged?.Invoke(this, EventArgs.Empty);
        RenderChart(viewModel);
        MarkerStatusText.Text = $"已清除当前标的与周期的 {removed} 个标记";
    }

    private void UpdateMarkerStatus(AnalysisSnapshot? snapshot = null)
    {
        var visibleCount = snapshot is null
            ? 0
            : _tradeMarkers.Count(marker =>
                marker.Instrument == snapshot.Instrument
                && marker.Timeframe == snapshot.Timeframe);
        ClearTradeMarkersButton.IsEnabled = visibleCount > 0;
        MarkerStatusText.Text = visibleCount > 0
            ? $"当前有 {visibleCount} 个标记 · Ctrl+左键买 · Ctrl+右键卖 · 双击框体删除"
            : "Ctrl+左键买 · Ctrl+右键卖 · 双击框体删除";
    }

    private void RenderCurrentChart()
    {
        if (DataContext is MainViewModel viewModel && IsInitialized)
        {
            RenderChart(viewModel);
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape
            && DataContext is MainViewModel viewModel)
        {
            viewModel.CloseDetailCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }
}
