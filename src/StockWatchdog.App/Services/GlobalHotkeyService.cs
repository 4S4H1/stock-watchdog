using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using StockWatchdog.Domain.Settings;

namespace StockWatchdog.App.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5231;
    private readonly HwndSource _source;
    private readonly nint _handle;
    private bool _registered;

    public GlobalHotkeyService(Window owner)
    {
        _handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle)
            ?? throw new InvalidOperationException("无法取得窗口消息源");
        _source.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    public HotkeySettings? Current { get; private set; }

    public bool Register(HotkeySettings settings, out string? error)
    {
        var previous = Current;
        Unregister();
        if (!RegisterHotKey(_handle, HotkeyId, (uint)settings.Modifiers, (uint)settings.VirtualKey))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            if (previous is not null
                && RegisterHotKey(
                    _handle,
                    HotkeyId,
                    (uint)previous.Modifiers,
                    (uint)previous.VirtualKey))
            {
                _registered = true;
                Current = previous;
            }

            return false;
        }

        _registered = true;
        Current = settings;
        error = null;
        return true;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    private void Unregister()
    {
        if (_registered)
        {
            _ = UnregisterHotKey(_handle, HotkeyId);
            _registered = false;
        }
    }

    private nint WndProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}

public static class HotkeyParser
{
    private const int ModAlt = 0x0001;
    private const int ModControl = 0x0002;
    private const int ModShift = 0x0004;
    private const int ModWin = 0x0008;
    private const int ModNoRepeat = 0x4000;

    public static bool TryParse(string? text, out HotkeySettings settings, out string? error)
    {
        settings = new HotkeySettings();
        error = null;
        var parts = text?.Split(
            '+',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length < 2)
        {
            error = "快捷键至少需要一个修饰键，例如 Ctrl+Alt+H";
            return false;
        }

        var modifiers = ModNoRepeat;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var modifier = parts[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModControl,
                "ALT" => ModAlt,
                "SHIFT" => ModShift,
                "WIN" or "WINDOWS" => ModWin,
                _ => 0
            };
            if (modifier == 0)
            {
                error = $"无法识别修饰键：{parts[index]}";
                return false;
            }

            modifiers |= modifier;
        }

        if ((modifiers & ~ModNoRepeat) == 0)
        {
            error = "快捷键必须包含 Ctrl、Alt、Shift 或 Win";
            return false;
        }

        var keyText = parts[^1].ToUpperInvariant();
        if (!Enum.TryParse<Key>(keyText, true, out var key) || key == Key.None)
        {
            error = $"无法识别按键：{keyText}";
            return false;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || key == Key.F12)
        {
            error = key == Key.F12 ? "F12 为系统调试保留键" : "该按键不能注册为全局快捷键";
            return false;
        }

        var display = string.Join(
            '+',
            new[]
            {
                (modifiers & ModControl) != 0 ? "Ctrl" : null,
                (modifiers & ModAlt) != 0 ? "Alt" : null,
                (modifiers & ModShift) != 0 ? "Shift" : null,
                (modifiers & ModWin) != 0 ? "Win" : null,
                keyText
            }.Where(x => x is not null));
        settings = new HotkeySettings(modifiers, virtualKey, display);
        return true;
    }
}
