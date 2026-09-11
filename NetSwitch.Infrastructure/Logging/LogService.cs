using System.Text;
using NetSwitch.Core.Abstractions;

namespace NetSwitch.Infrastructure.Logging;

/// <summary>
/// 轮转日志：单文件超过上限即轮转，并按数量与总大小自动清理旧日志。
/// </summary>
public sealed class LogService : ILogger
{
    private const long MaxFileBytes = 512 * 1024;      // 单文件 512 KB
    private const long MaxTotalBytes = 5 * 1024 * 1024; // 总大小 5 MB
    private const int MaxFiles = 10;                    // 保留最近 10 个文件

    private readonly object _lock = new();
    private readonly string _directory;
    private string _filePath;

    public LogService(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NetSwitch",
            "logs");
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, $"netswitch-{DateTime.Now:yyyyMMdd}.log");
    }

    /// <summary>日志目录路径，供日志查看窗口读取。</summary>
    public string DirectoryPath => _directory;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
        => Write("ERROR", exception is null ? message : $"{message} {exception}");

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            try
            {
                RotateIfNeeded();
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(_filePath, line, Encoding.UTF8);
                Cleanup();
            }
            catch
            {
                // 日志失败不影响主流程。
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (File.Exists(_filePath) && new FileInfo(_filePath).Length > MaxFileBytes)
        {
            var archived = Path.Combine(
                _directory,
                $"netswitch-{DateTime.Now:yyyyMMddHHmmss}.log");
            File.Move(_filePath, archived);
        }
    }

    private void Cleanup()
    {
        var files = Directory.GetFiles(_directory, "*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        // 按数量清理。
        foreach (var file in files.Skip(MaxFiles))
        {
            TryDelete(file);
        }

        // 按总大小清理（从最旧删起）。
        var remaining = Directory.GetFiles(_directory, "*.log")
            .OrderBy(File.GetLastWriteTimeUtc)
            .ToList();
        long total = remaining.Sum(f => new FileInfo(f).Length);
        foreach (var file in remaining)
        {
            if (total <= MaxTotalBytes)
            {
                break;
            }

            total -= new FileInfo(file).Length;
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 忽略。
        }
    }
}
