using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WifiWatchdog;

public sealed class WatchdogLog
{
    private static readonly WatchdogJsonContext StatusJsonContext = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    });

    private readonly Lock _syncRoot = new();
    private readonly string _logDirectory;
    private readonly string _statusPath;

    public WatchdogLog()
    {
        _logDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "WifiWatchdog",
            "logs");
        _statusPath = Path.Combine(Path.GetDirectoryName(_logDirectory)!, "status.json");
        Directory.CreateDirectory(_logDirectory);
    }

    public void Write(string level, string state, string message, int? visibleBssCount = null, int? targetBssCount = null)
    {
        var timestamp = DateTimeOffset.Now;
        var status = new WatchdogStatus
        {
            Timestamp = timestamp,
            State = state,
            Message = message,
            VisibleBssCount = visibleBssCount,
            TargetBssCount = targetBssCount
        };

        try
        {
            lock (_syncRoot)
            {
                var logPath = Path.Combine(_logDirectory, $"wifi-watchdog-{timestamp:yyyyMMdd}.log");
                var line = $"{timestamp:O} [{level}] [{state}] {message}{Environment.NewLine}";
                File.AppendAllText(logPath, line, Encoding.UTF8);
                File.WriteAllText(
                    _statusPath,
                    JsonSerializer.Serialize(status, StatusJsonContext.WatchdogStatus),
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never stop the recovery loop.
        }
    }
}
