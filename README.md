# NetSwitch

一个运行在 Windows 上的网络适配器优先级自动仲裁器。它枚举本机的物理网络适配器（含曾经接入过的），让你为它们设置优先级；开启自动仲裁后，程序会始终**启用优先级最高的可用适配器，并禁用优先级较低的在线适配器**，确保网络流量走你指定的网卡。

## 功能

- **枚举网络适配器**：通过 WMI（`MSFT_NetAdapter` + `Win32_NetworkAdapter`）读取当前活动网卡与历史网卡，曾经接入过的适配器也会保留在列表中。
- **优先级排序**：为每张网卡设定优先级，一键手动切换或全自动仲裁。
- **自动仲裁**：周期性轮询，保证在已管理范围内只有一个「目标激活网卡」处于启用状态。
  - 内置冷却期与「状态观察期」去抖，避免设备插拔 / WMI 状态抖动引起的反复切换。
  - 手动切换绕过冷却，立即生效；并同步状态基线，不会被随后的自动仲裁撤销。
- **安全护栏**：候选为空、全部断开、或关闭自动模式时，绝不执行任何启停操作。
- **虚拟网卡默认忽略**：首次发现的虚拟网卡（如 VPN、虚拟交换机）自动标记为忽略，不被仲裁触碰。
- **系统托盘 + 桌面通知**：关闭窗口后驻留后台运行，托盘图标可打开窗口、切换自动模式、退出。
- **单实例**：重复启动会唤起已运行的实例窗口，而不是启动第二个进程。
- **开机自启**：通过计划任务实现，随 Windows 启动。

## 技术栈

- .NET 10（`net10.0-windows`）、WPF、C# 12
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)（MVVM）
- [System.Management](https://www.nuget.org/packages/System.Management)（WMI）
- [H.NotifyIcon.Wpf](https://www.nuget.org/packages/H.NotifyIcon.Wpf)（托盘 + 通知）

## 架构

分层结构，从 UI 到数据访问依次为：

```
NetSwitch.App              WPF 界面、托盘、轮询监控（组合根）
   └── NetSwitch.Infrastructure   WMI 数据访问、JSON 持久化、日志
         └── NetSwitch.Core        仲裁状态机、领域模型、抽象接口
```

- 业务逻辑（`ArbitrationService`）只依赖抽象接口（`IAdapterRepository`、`IConfigStore`），可独立测试。
- 配置持久化到 `%APPDATA%\NetSwitch\config.json`。

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
dotnet build NetSwitch.sln -c Release
```

运行单元测试：

```bash
dotnet test NetSwitch.sln -c Release
```

发布单文件自包含 exe（win-x64）：

```bash
dotnet publish NetSwitch.App/NetSwitch.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

## 使用说明

1. 以**管理员身份**运行（程序清单声明了 `requireAdministrator`，需要权限才能启停网卡）。
2. 在列表中为每张网卡设置优先级，勾选「自动仲裁」。
3. 关闭窗口后程序驻留托盘；右键托盘图标可退出。

## 许可证

[MIT](LICENSE)
