namespace NetSwitch.Core.Models;

/// <summary>
/// 应用配置根对象，对应 <c>%APPDATA%\NetSwitch\config.json</c>。
/// </summary>
public sealed class AppConfig
{
    /// <summary>自动仲裁开关；关闭时仅刷新状态展示，不执行 Enable/Disable。</summary>
    public bool AutoArbitrate { get; set; } = true;

    /// <summary>轮询间隔（秒）。</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>开机自启开关（由系统计划任务的真实状态校正）。</summary>
    public bool AutoStartEnabled { get; set; }

    /// <summary>
    /// 静默启动：为 true 时启动后不显示主窗口，直接驻留托盘。
    /// 计划任务固定以 <c>--silent</c> 拉起，因此自启场景总是静默；
    /// 本项决定用户**双击 exe** 时的行为。命令行 <c>--silent</c>/<c>--show</c> 优先于本项。
    /// </summary>
    public bool StartSilently { get; set; } = true;

    public bool NotificationsEnabled { get; set; } = true;

    public List<KnownAdapter> KnownAdapters { get; set; } = new();
}
