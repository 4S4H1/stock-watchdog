using System.Windows;

namespace StockWatchdog.App.Services;

public sealed class PrivacyController
{
    private readonly List<Window> _windows = [];
    private readonly Dictionary<Window, bool> _visibilityBeforeHide = [];
    private int _hiddenAlertCount;

    public bool IsHidden { get; private set; }

    public event EventHandler? StateChanged;

    public void Register(Window window)
    {
        if (!_windows.Contains(window))
        {
            _windows.Add(window);
        }
    }

    public void Unregister(Window window)
    {
        _windows.Remove(window);
        _visibilityBeforeHide.Remove(window);
    }

    public void Toggle()
    {
        if (IsHidden)
        {
            Restore();
        }
        else
        {
            HideAll();
        }
    }

    public void HideAll()
    {
        if (IsHidden)
        {
            return;
        }

        _visibilityBeforeHide.Clear();
        foreach (var window in _windows.ToArray())
        {
            _visibilityBeforeHide[window] = window.IsVisible;
            if (window.IsVisible)
            {
                window.Hide();
            }
        }

        IsHidden = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public int Restore()
    {
        if (!IsHidden)
        {
            return 0;
        }

        IsHidden = false;
        foreach (var pair in _visibilityBeforeHide.ToArray())
        {
            if (pair.Value)
            {
                pair.Key.Show();
            }
        }

        _visibilityBeforeHide.Clear();
        var count = Interlocked.Exchange(ref _hiddenAlertCount, 0);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return count;
    }

    public void NoteHiddenAlert() => Interlocked.Increment(ref _hiddenAlertCount);
}
