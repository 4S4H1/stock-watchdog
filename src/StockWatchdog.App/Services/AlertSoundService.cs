using System.Runtime.InteropServices;

namespace StockWatchdog.App.Services;

public sealed class AlertSoundService
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndAlias = 0x00010000;

    public void Play() =>
        _ = PlaySound("SystemExclamation", 0, SndAlias | SndAsync | SndNoDefault);

    public void Stop() =>
        _ = PlaySound(null, 0, 0);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? sound, nint module, uint flags);
}
