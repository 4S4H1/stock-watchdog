namespace StockWatchdog.Domain.Market;

public static class BarIntegrity
{
    public static bool HasValidPrices(Bar bar) =>
        bar.Open > 0
        && bar.High > 0
        && bar.Low > 0
        && bar.Close > 0
        && bar.Low <= bar.High
        && bar.Low <= bar.Open
        && bar.Low <= bar.Close
        && bar.High >= bar.Open
        && bar.High >= bar.Close;
}
