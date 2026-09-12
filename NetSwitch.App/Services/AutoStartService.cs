using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using NetSwitch.Core.Abstractions;

namespace NetSwitch.App.Services;

/// <summary>
/// 开机自启管理，基于计划任务（AtLogon + 最高权限 + 静默参数 + 登录后延迟）。
///
/// V1.1 变更（规格 §10.2 / §10.3）：
/// <list type="bullet">
/// <item>任务命令行追加 <c>--silent</c>（静默启动）与 <c>/delay 0000:15</c>（等网络栈就绪）。</item>
/// <item><see cref="IsEnabled"/> 不再只判断「任务是否存在」，而是解析任务 XML
/// 校验 exe 路径、<c>--silent</c> 参数与 <c>HighestAvailable</c> 运行级别。</item>
/// <item>新增 <see cref="EnsureUpToDate"/>：任务存在但已失效（exe 被移动/升级、参数过期）时自动重建。</item>
/// </list>
/// </summary>
public sealed class AutoStartService
{
    private const string TaskName = "NetSwitchAutoStart";

    /// <summary>登录后延迟启动，避开网络栈/WMI 未就绪与登录瞬间的资源争抢。</summary>
    private const string DelaySpec = "0000:15";

    private readonly string _exePath;
    private readonly ILogger? _logger;

    public AutoStartService(ILogger? logger = null)
    {
        _logger = logger;
        _exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "NetSwitch.App.exe");
    }

    /// <summary>当前进程的可执行文件路径（计划任务指向的目标）。</summary>
    public string ExecutablePath => _exePath;

    /// <summary>任务是否存在**且**定义与当前 exe 匹配（路径 / <c>--silent</c> / 最高权限）。</summary>
    public bool IsEnabled()
        => QueryTaskDefinition() is { } definition && definition.IsUpToDate(_exePath);

    /// <summary>创建或覆盖计划任务。</summary>
    public void Enable()
        => Run("schtasks",
            $"/create /tn \"{TaskName}\" /tr \"\\\"{_exePath}\\\" {StartupOptions.SilentSwitch}\" " +
            $"/sc onlogon /rl highest /delay {DelaySpec} /f");

    public void Disable()
        => Run("schtasks", $"/delete /tn \"{TaskName}\" /f");

    /// <summary>
    /// 校验并（必要时）修复计划任务，返回修复后自启是否处于开启状态。
    /// 任务不存在 → 返回 <c>false</c>；存在但失效 → 自动重建后返回 <c>true</c>。
    /// </summary>
    public bool EnsureUpToDate()
    {
        var definition = QueryTaskDefinition();
        if (definition is null)
        {
            return false;
        }

        if (definition.IsUpToDate(_exePath))
        {
            return true;
        }

        _logger?.Warn(
            $"计划任务 {TaskName} 已失效，正在重建。" +
            $"期望 exe={_exePath} 参数含 {StartupOptions.SilentSwitch} 运行级别=HighestAvailable；" +
            $"实际 command={definition.Command} args={definition.Arguments} runLevel={definition.RunLevel}");

        Enable();
        return true;
    }

    /// <summary>查询并解析任务定义；任务不存在或查询失败时返回 <c>null</c>。</summary>
    private TaskDefinition? QueryTaskDefinition()
    {
        var psi = new ProcessStartInfo("schtasks", $"/query /tn \"{TaskName}\" /xml")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        var xml = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            return null; // 任务不存在（或无权访问）。
        }

        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);

            // 任务 XML 带默认命名空间；按 LocalName 匹配以规避命名空间差异。
            string? Value(string localName) => doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

            return new TaskDefinition(
                Value("Command") ?? string.Empty,
                Value("Arguments") ?? string.Empty,
                Value("RunLevel") ?? string.Empty);
        }
        catch (System.Xml.XmlException ex)
        {
            // 解析失败按「需要修复」处理：返回空定义，IsUpToDate 必为 false。
            _logger?.Warn($"解析计划任务 XML 失败，将尝试重建任务: {ex.Message}");
            return new TaskDefinition(string.Empty, string.Empty, string.Empty);
        }
    }

    private static void Run(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException($"无法启动命令：{fileName}");
        }

        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"：{stderr.Trim()}";
            throw new InvalidOperationException($"命令失败（{fileName}），退出码 {process.ExitCode}{detail}");
        }
    }

    /// <summary>计划任务定义（只取校验所需字段）。</summary>
    private sealed record TaskDefinition(string Command, string Arguments, string RunLevel)
    {
        /// <summary>校验 exe 路径、<c>--silent</c> 参数与最高权限运行级别三项。</summary>
        public bool IsUpToDate(string expectedExePath)
        {
            if (string.IsNullOrWhiteSpace(Command))
            {
                return false;
            }

            return string.Equals(NormalizePath(Command), NormalizePath(expectedExePath), StringComparison.OrdinalIgnoreCase)
                && Arguments.Contains(StartupOptions.SilentSwitch, StringComparison.OrdinalIgnoreCase)
                && RunLevel.Contains("Highest", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
        {
            var trimmed = path.Trim().Trim('"').Trim();
            try
            {
                return Path.GetFullPath(trimmed).TrimEnd('\\');
            }
            catch (Exception)
            {
                // 路径含非法字符时按原串比较，交由上层判定为「不匹配」并重建任务。
                return trimmed;
            }
        }
    }
}
