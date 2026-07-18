using StockWatchdog.Domain.Market;
using StockWatchdog.Infrastructure.MarketData;
using StockWatchdog.Infrastructure.Persistence;

if (args.Length >= 2 && args[0].Equals("--cache", StringComparison.OrdinalIgnoreCase))
{
    if (!InstrumentId.TryParse(args[1], out var cachedInstrument))
    {
        Console.Error.WriteLine("缓存检查需要有效的 6 位代码。");
        return 2;
    }

    var databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StockWatchdog",
        "stock-watchdog.db");
    var repository = new SqliteAppRepository(databasePath);
    await repository.InitializeAsync();
    foreach (var timeframe in Enum.GetValues<Timeframe>())
    {
        var cachedBars = await repository.GetCachedBarsAsync(cachedInstrument, timeframe, 500);
        var invalidBars = cachedBars.Where(IsInvalidOhlc).ToArray();
        Console.WriteLine($"{timeframe} cached={cachedBars.Count} invalid={invalidBars.Length}");
        foreach (var bar in invalidBars.Take(10))
        {
            Console.WriteLine(
                $"  {bar.StartTime:O} O={bar.Open} H={bar.High} L={bar.Low} C={bar.Close} source={bar.Source}");
        }
    }

    return 0;
}

var requestedCodes = args.Length == 0
    ? new[] { "600519", "000001", "510300" }
    : args;
var instruments = requestedCodes
    .Select(code => InstrumentId.TryParse(code, out var instrument) ? instrument : (InstrumentId?)null)
    .Where(x => x is not null)
    .Select(x => x!.Value)
    .ToArray();
if (instruments.Length == 0)
{
    Console.Error.WriteLine("请提供至少一个有效的沪深 6 位代码。");
    return 2;
}

using var primaryClient = new HttpClient();
using var fallbackClient = new HttpClient();
var primary = new EastMoneyMarketDataProvider(primaryClient);
var fallback = new TencentSnapshotProvider(fallbackClient);
var provider = new ResilientMarketDataProvider(primary, fallback);

var quotes = await provider.GetQuotesAsync(instruments, CancellationToken.None);
Console.WriteLine(
    $"quotes success={quotes.IsSuccess} source={quotes.Source} quality={quotes.Quality} count={quotes.Data.Count}");
foreach (var quote in quotes.Data)
{
    Console.WriteLine(
        $"{quote.Instrument} {quote.Name} price={quote.Price:0.###} quality={quote.Quality} age={(DateTimeOffset.Now - quote.SourceTime).TotalSeconds:0.0}s flags={quote.QualityFlags}");
}

var sample = instruments[0];
var allBarsHealthy = true;
foreach (var timeframe in new[] { Timeframe.Minute1, Timeframe.Minute60, Timeframe.Day })
{
    var bars = await provider.GetBarsAsync(sample, timeframe, 60, CancellationToken.None);
    var invalidCount = bars.Data.Count(IsInvalidOhlc);
    Console.WriteLine(
        $"{timeframe} success={bars.IsSuccess} count={bars.Data.Count} final={bars.Data.Count(x => x.IsFinal)} invalid={invalidCount} error={bars.Error ?? "--"}");
    allBarsHealthy &= bars.IsSuccess && bars.Data.Count > 0 && invalidCount == 0;
}

return quotes.IsSuccess && quotes.Data.Count > 0 && allBarsHealthy ? 0 : 1;

static bool IsInvalidOhlc(Bar bar) =>
    bar.Low > Math.Min(bar.Open, bar.Close)
    || bar.High < Math.Max(bar.Open, bar.Close)
    || bar.Low > bar.High
    || bar.Open <= 0
    || bar.Close <= 0;
