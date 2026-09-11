namespace NetSwitch.Core.Models;

/// <summary>
/// 应用配置根对象，对应 <c>%APPDATA%\NetSwitch\config.json</c>。
/// </summary>
public sealed class AppConfig
{
    /// <summary>自动仲裁开关；关闭时仅刷新状态展示，不执行 Enable/Disable。</summary>
    public bool AutoArbitrate { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 2;

    public bool AutoStartEnabled { get; set; }

    public bool NotificationsEnabled { get; set; } = true;

    public List<KnownAdapter> KnownAdapters { get; set; } = new();
}
