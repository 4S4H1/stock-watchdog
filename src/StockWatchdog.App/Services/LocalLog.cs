using System.IO;

namespace StockWatchdog.App.Services;

public sealed class LocalLog
{
    private readonly string _directory;
    private readonly Lock _gate = new();

    public LocalLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    public void Write(string level, string message)
    {
        var safe = message.ReplaceLineEndings(" ");
        var line = $"{DateTimeOffset.Now:O} [{level}] {safe}{Environment.NewLine}";
        lock (_gate)
        {
            File.AppendAllText(
                Path.Combine(_directory, $"{DateTime.Today:yyyyMMdd}.log"),
                line);
        }
    }
}
