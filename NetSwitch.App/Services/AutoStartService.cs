using System.Diagnostics;
using System.IO;

namespace NetSwitch.App.Services;

/// <summary>
/// 开机自启管理，基于计划任务（AtLogon + 最高权限）。
/// </summary>
public sealed class AutoStartService
{
    private const string TaskName = "NetSwitchAutoStart";

    private readonly string _exePath;

    public AutoStartService()
    {
        _exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "NetSwitch.App.exe");
    }

    public bool IsEnabled()
    {
        var psi = new ProcessStartInfo("schtasks", $"/query /tn \"{TaskName}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(psi);
        process!.WaitForExit();
        return process.ExitCode == 0;
    }

    public void Enable()
    {
        Run("schtasks", $"/create /tn \"{TaskName}\" /tr \"\\\"{_exePath}\\\"\" /sc onlogon /rl highest /f");
    }

    public void Disable()
    {
        Run("schtasks", $"/delete /tn \"{TaskName}\" /f");
    }

    private static void Run(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi);
        process!.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"命令失败（{fileName}），退出码 {process.ExitCode}");
        }
    }
}
