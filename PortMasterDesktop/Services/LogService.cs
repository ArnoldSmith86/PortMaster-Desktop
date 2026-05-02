using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace PortMasterDesktop.Services;

/// <summary>
/// Application-wide file logger. Never writes authentication tokens, API keys, or credentials.
/// Log files rotate under %LOCALAPPDATA%/portmaster-desktop/logs/.
/// Accessible as LogService.Instance for static contexts (e.g. HTTP handler).
/// </summary>
public class LogService
{
    private static LogService? _instance;
    // Returns the singleton, creating a no-op instance if Initialize() hasn't been called yet.
    public static LogService Instance => _instance ??= new LogService();

    private StreamWriter? _writer;
    private readonly object _lock = new();
    public string? LogFilePath { get; private set; }

    public LogService() { }

    public void Initialize()
    {
        _instance = this;

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "portmaster-desktop", "logs");
        Directory.CreateDirectory(logDir);

        // Keep the 9 most recent log files
        try
        {
            var old = Directory.GetFiles(logDir, "portmaster-*.log")
                .OrderBy(f => f).ToArray();
            foreach (var f in old.Take(Math.Max(0, old.Length - 9)))
                File.Delete(f);
        }
        catch { }

        LogFilePath = Path.Combine(logDir, $"portmaster-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(LogFilePath, append: false, Encoding.UTF8) { AutoFlush = true };

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        Info("=== PortMaster Desktop ===");
        Info($"Version:         {version}");
        Info($"OS:              {Environment.OSVersion}");
        Info($"Is64Bit:         {Environment.Is64BitOperatingSystem}");
        Info($".NET runtime:    {Environment.Version}");
        Info($"ProcessorCount:  {Environment.ProcessorCount}");
        Info($"LocalAppData:    {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}");
        Info($"Log file:        {LogFilePath}");
        Info("=========================");
    }

    public void Info(string message)  => Write("INFO ", message);
    public void Warn(string message)  => Write("WARN ", message);

    public void Error(string message, Exception? ex = null)
    {
        Write("ERROR", message);
        if (ex == null) return;
        Write("ERROR", $"  {ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException != null)
            Write("ERROR", $"  Inner: {ex.InnerException.Message}");
    }

    /// <summary>Log an HTTP result. Query strings are stripped to avoid leaking tokens.</summary>
    public void LogHttp(string method, string url, int statusCode, TimeSpan elapsed, long? responseBytes = null)
    {
        var safeUrl  = SanitizeUrl(url);
        var bytesStr = responseBytes.HasValue ? $"  {FormatBytes(responseBytes.Value)}" : "";
        Write("HTTP ", $"{method,-6} {statusCode}  {elapsed.TotalMilliseconds,6:F0}ms{bytesStr}  {safeUrl}");
    }

    public void LogHttpError(string method, string url, Exception ex, TimeSpan elapsed)
    {
        var safeUrl = SanitizeUrl(url);
        Write("HTTP ", $"{method,-6} ERR   {elapsed.TotalMilliseconds,6:F0}ms  {safeUrl}  [{ex.GetType().Name}: {ex.Message}]");
    }

    public void Section(string heading)
        => Write("INFO ", $"--- {heading} ---");

    private void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
        lock (_lock)
        {
            try { _writer?.WriteLine(line); }
            catch { }
        }
    }

    // ── URL sanitization ─────────────────────────────────────────────────────

    // itch.io embeds the API key as the 4th path segment: /api/1/{KEY}/...
    private static readonly Regex ItchKeyInPath =
        new(@"(https?://itch\.io/api/1/)[^/?#]+(/.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Catch any remaining ?param=... query string that could contain tokens
    private static string SanitizeUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "[url]";
        try
        {
            // 1. Redact itch.io API key from the URL path
            if (url.Contains("itch.io/api/"))
                url = ItchKeyInPath.Replace(url, "$1[key]$2");

            // 2. Strip the query string — covers GOG refresh_token, itch api_key params, etc.
            var q = url.IndexOf('?');
            return q >= 0 ? url[..q] + "[?…]" : url;
        }
        catch { return "[url]"; }
    }

    private static string FormatBytes(long b)
    {
        if (b >= 1_048_576) return $"{b / 1_048_576.0:F1} MB";
        if (b >= 1024)      return $"{b / 1024.0:F1} KB";
        return $"{b} B";
    }
}
