namespace NetSwitch.Core.Models;

/// <summary>
/// 应用配置根对象，对应 <c>%APPDATA%\NetSwitch\config.json</c>。
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// 首次运行（配置文件中尚无 <c>startSilently</c> 字段）时采用的静默启动默认值。
    ///
    /// 取 <c>false</c> 的理由（规格 §15 方案 2）：默认 <c>true</c> 会让「双击 exe 就是想看界面」
    /// 的用户以为程序没启动（V1.2.0 实测复现过）。首次给窗口，之后完全按用户选择。
    /// 开机自启场景不受影响——计划任务固定带 <c>--silent</c>，命令行优先级高于配置项。
    /// </summary>
    public const bool FirstRunStartSilently = false;

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
    ///
    /// <c>null</c> 表示配置文件中尚无该字段（首次运行，或从 V1.2.0 之前的版本升级上来），
    /// 此时按 <see cref="ResolveStartSilently"/> 取 <see cref="FirstRunStartSilently"/>。
    /// 该值会在首次保存时物化为具体布尔值（见 <c>JsonConfigStore.Load</c>）。
    /// </summary>
    public bool? StartSilently { get; set; }

    public bool NotificationsEnabled { get; set; } = true;

    public List<KnownAdapter> KnownAdapters { get; set; } = new();

    /// <summary>解析静默启动开关：未设置时取首次运行默认值。</summary>
    public bool ResolveStartSilently() => StartSilently ?? FirstRunStartSilently;
}
