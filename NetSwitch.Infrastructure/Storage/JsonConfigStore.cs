using System.Text.Json;
using System.Text.Json.Serialization;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Infrastructure.Storage;

/// <summary>
/// JSON 配置存储，读写 <c>%APPDATA%\NetSwitch\config.json</c>。
/// 写入采用「临时文件 + 原子替换」；读取区分损坏（备份回退）与 IO 错误（返回缓存），
/// 避免瞬时 IO 失败导致空配置覆盖用户数据。
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly string _directory;
    private readonly string _filePath;
    private AppConfig? _cached;

    public JsonConfigStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(ResolveAppDataDirectory(), "NetSwitch");
        _filePath = Path.Combine(_directory, "config.json");
    }

    /// <summary>
    /// 解析漫游 AppData 目录。某些受限会话（服务账户、环境变量被裁剪的沙箱）
    /// 下 <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> 会返回空串，
    /// 此时回退到 USERPROFILE，避免把配置写到当前工作目录这种不可预期的位置。
    /// </summary>
    private static string ResolveAppDataDirectory()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var profile = Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(profile))
        {
            return Path.Combine(profile, "AppData", "Roaming");
        }

        return AppContext.BaseDirectory;
    }

    public AppConfig Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return NewDefault();
                }

                var json = File.ReadAllText(_filePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, Options) ?? NewDefault();
                config.KnownAdapters ??= new List<KnownAdapter>();
                _cached = config;
                return config;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // 文件确实损坏：备份并回退默认值。
                BackupCorruptFile();
                return NewDefault();
            }
            catch (IOException)
            {
                // 文件被临时锁定等 IO 错误：返回内存缓存，避免后续 Save 用空配置覆盖原文件。
                return _cached ?? NewDefault();
            }
        }
    }

    public void Save(AppConfig config)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(config, Options);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _filePath, overwrite: true);
            _cached = config;
        }
    }

    private AppConfig NewDefault()
    {
        var fresh = new AppConfig();
        _cached = fresh;
        return fresh;
    }

    private void BackupCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Move(_filePath, _filePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}");
            }
        }
        catch
        {
            // 备份失败不阻断主流程。
        }
    }
}
