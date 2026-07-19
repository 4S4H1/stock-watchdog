using StockWatchdog.App.Infrastructure;
using StockWatchdog.Application.Analysis;
using StockWatchdog.Domain.Analysis;
using StockWatchdog.Domain.Market;

namespace StockWatchdog.App.ViewModels;

public sealed class WatchRowViewModel : ObservableObject
{
    private string _name;
    private string _marketName;
    private decimal _price;
    private decimal _changePercent;
    private DateTimeOffset? _sourceTime;
    private IReadOnlyList<decimal> _sparklineValues = [];
    private SparklineDirection _sparklineDirection;
    private MarketDataQuality _quality = MarketDataQuality.WarmingUp;
    private string _signalText = "等待数据";
    private PatternDirection _signalDirection = PatternDirection.Neutral;
    private int? _buyScore;
    private int? _sellScore;
    private string _tSignalSummary = "做 T 评分预热中";
    private TSignalState _tSignalState = TSignalState.Unavailable;

    public WatchRowViewModel(WatchItem item)
    {
        Item = item;
        _marketName = string.IsNullOrWhiteSpace(item.Name) ? item.Instrument.Code : item.Name;
        _name = ResolveDisplayName(item, _marketName);
    }

    public WatchItem Item { get; private set; }

    public InstrumentId Instrument => Item.Instrument;

    public string Code => Instrument.ToString();

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public decimal Price
    {
        get => _price;
        private set
        {
            if (SetProperty(ref _price, value))
            {
                OnPropertyChanged(nameof(PriceText));
            }
        }
    }

    public string PriceText => Price > 0 ? Price.ToString("0.000") : "--";

    public decimal ChangePercent
    {
        get => _changePercent;
        private set
        {
            if (SetProperty(ref _changePercent, value))
            {
                OnPropertyChanged(nameof(ChangePercentText));
                OnPropertyChanged(nameof(IsPositive));
                OnPropertyChanged(nameof(IsNegative));
            }
        }
    }

    public string ChangePercentText =>
        Price > 0 ? $"{ChangePercent:+0.00;-0.00;0.00}%" : "--";

    public bool IsPositive => ChangePercent > 0;

    public bool IsNegative => ChangePercent < 0;

    public DateTimeOffset? SourceTime
    {
        get => _sourceTime;
        private set
        {
            if (SetProperty(ref _sourceTime, value))
            {
                OnPropertyChanged(nameof(UpdateTimeText));
            }
        }
    }

    public string UpdateTimeText => SourceTime?.ToString("HH:mm:ss") ?? "--";

    public IReadOnlyList<decimal> SparklineValues
    {
        get => _sparklineValues;
        private set => SetProperty(ref _sparklineValues, value);
    }

    public bool IsSparklineRising =>
        _sparklineDirection == SparklineDirection.Rising;

    public bool IsSparklineFalling =>
        _sparklineDirection == SparklineDirection.Falling;

    public MarketDataQuality Quality
    {
        get => _quality;
        private set
        {
            if (SetProperty(ref _quality, value))
            {
                OnPropertyChanged(nameof(QualityText));
                OnPropertyChanged(nameof(IsDataHealthy));
            }
        }
    }

    public string QualityText => Quality switch
    {
        MarketDataQuality.Healthy => "正常",
        MarketDataQuality.Delayed => "延迟",
        MarketDataQuality.Stale => "过期",
        MarketDataQuality.Divergent => "冲突",
        MarketDataQuality.Unavailable => "不可用",
        MarketDataQuality.WarmingUp => "预热",
        _ => "未知"
    };

    public bool IsDataHealthy => Quality == MarketDataQuality.Healthy;

    public string SignalText
    {
        get => _signalText;
        private set => SetProperty(ref _signalText, value);
    }

    public PatternDirection SignalDirection
    {
        get => _signalDirection;
        private set
        {
            if (SetProperty(ref _signalDirection, value))
            {
                OnPropertyChanged(nameof(IsBullishSignal));
                OnPropertyChanged(nameof(IsBearishSignal));
            }
        }
    }

    public bool IsBullishSignal => SignalDirection == PatternDirection.Bullish;

    public bool IsBearishSignal => SignalDirection == PatternDirection.Bearish;

    public int? BuyScore
    {
        get => _buyScore;
        private set
        {
            if (SetProperty(ref _buyScore, value))
            {
                OnPropertyChanged(nameof(BuyScoreText));
            }
        }
    }

    public int? SellScore
    {
        get => _sellScore;
        private set
        {
            if (SetProperty(ref _sellScore, value))
            {
                OnPropertyChanged(nameof(SellScoreText));
            }
        }
    }

    public string BuyScoreText => BuyScore?.ToString() ?? "--";

    public string SellScoreText => SellScore?.ToString() ?? "--";

    public string TSignalSummary
    {
        get => _tSignalSummary;
        private set => SetProperty(ref _tSignalSummary, value);
    }

    public TSignalState TScoreState
    {
        get => _tSignalState;
        private set
        {
            if (SetProperty(ref _tSignalState, value))
            {
                OnPropertyChanged(nameof(IsBuyCandidate));
                OnPropertyChanged(nameof(IsSellCandidate));
                OnPropertyChanged(nameof(IsTSignalUnavailable));
            }
        }
    }

    public bool IsBuyCandidate =>
        TScoreState is TSignalState.BuyCandidate or TSignalState.WatchBuy;

    public bool IsSellCandidate =>
        TScoreState is TSignalState.SellCandidate or TSignalState.WatchSell;

    public bool IsTSignalUnavailable => TScoreState == TSignalState.Unavailable;

    public void UpdateQuote(QuoteSnapshot quote)
    {
        if (quote.Instrument != Instrument)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(quote.Name))
        {
            _marketName = quote.Name;
        }

        Name = ResolveDisplayName(Item, _marketName);
        Price = quote.Price;
        ChangePercent = quote.ChangePercent;
        SourceTime = quote.SourceTime;
        Quality = quote.Quality;
    }

    public void UpdateSparkline(AnalysisSnapshot snapshot)
    {
        if (snapshot.Instrument != Instrument)
        {
            return;
        }

        var series = SparklineSeriesBuilder.Build(snapshot);
        SparklineValues = series.Values;
        if (_sparklineDirection != series.Direction)
        {
            _sparklineDirection = series.Direction;
            OnPropertyChanged(nameof(IsSparklineRising));
            OnPropertyChanged(nameof(IsSparklineFalling));
        }
    }

    public void UpdateAnalysis(AnalysisSnapshot snapshot)
    {
        if (snapshot.Instrument != Instrument)
        {
            return;
        }

        var finding = snapshot.Findings.OrderByDescending(x => x.Score).FirstOrDefault();
        SignalText = snapshot.DataQuality is MarketDataQuality.Healthy
            or MarketDataQuality.WarmingUp
            ? finding?.DisplayName ?? snapshot.StatusText
            : "数据异常，信号暂停";
        SignalDirection = finding?.Direction ?? PatternDirection.Neutral;
    }

    public void UpdateTSignal(TSignalSnapshot snapshot)
    {
        if (snapshot.Instrument != Instrument)
        {
            return;
        }

        BuyScore = snapshot.BuyScore;
        SellScore = snapshot.SellScore;
        TSignalSummary = snapshot.Summary;
        TScoreState = snapshot.State;
    }

    public void ReplaceItem(WatchItem item)
    {
        Item = item;
        OnPropertyChanged(nameof(Item));
        Name = ResolveDisplayName(item, _marketName);
    }

    private static string ResolveDisplayName(WatchItem item, string marketName) =>
        !string.IsNullOrWhiteSpace(item.CustomName)
            ? item.CustomName.Trim()
            : string.IsNullOrWhiteSpace(marketName)
                ? item.Instrument.Code
                : marketName;
}
