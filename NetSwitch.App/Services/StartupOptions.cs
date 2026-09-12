namespace NetSwitch.App.Services;

/// <summary>命令行指定的启动模式。</summary>
public enum StartupMode
{
    /// <summary>未指定，采用配置项 <c>StartSilently</c>。</summary>
    Unspecified,

    /// <summary>强制静默：不显示主窗口，仅驻留托盘。</summary>
    Silent,

    /// <summary>强制显示主窗口。</summary>
    Show,
}

/// <summary>
/// 启动参数解析（规格 §10.3）。优先级：命令行 &gt; 配置项。
/// 计划任务固定以 <c>--silent</c> 拉起进程，因此自启场景总是静默；
/// <c>--show</c> 用于「这次我要看界面」的场景。
/// </summary>
public static class StartupOptions
{
    public const string SilentSwitch = "--silent";
    public const string ShowSwitch = "--show";

    /// <summary>解析命令行中的启动模式；同时出现时 <c>--silent</c> 优先（静默更保守）。</summary>
    public static StartupMode ParseMode(IEnumerable<string>? args)
    {
        if (args is null)
        {
            return StartupMode.Unspecified;
        }

        var mode = StartupMode.Unspecified;
        foreach (var raw in args)
        {
            var arg = raw.Trim();
            if (arg.Equals(SilentSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return StartupMode.Silent;
            }

            if (arg.Equals(ShowSwitch, StringComparison.OrdinalIgnoreCase))
            {
                mode = StartupMode.Show;
            }
        }

        return mode;
    }

    /// <summary>合并命令行与配置，得到本次启动是否静默。</summary>
    public static bool ResolveSilent(IEnumerable<string>? args, bool configStartSilently)
        => ParseMode(args) switch
        {
            StartupMode.Silent => true,
            StartupMode.Show => false,
            _ => configStartSilently,
        };
}
