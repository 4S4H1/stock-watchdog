using System.Windows;
using System.Windows.Threading;
using StockWatchdog.Domain.Alerts;

namespace StockWatchdog.App.Views;

public partial class AlertToastWindow : Window
{
    private readonly DispatcherTimer _timer;

    public AlertToastWindow(AlertEvent alert, TimeSpan visibleFor)
    {
        InitializeComponent();
        DataContext = new
        {
            alert.Title,
            alert.Message,
            TimeText = alert.TriggeredAt.ToString("HH:mm:ss")
        };
        _timer = new DispatcherTimer { Interval = visibleFor };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Close();
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }
}
