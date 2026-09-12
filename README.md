# NetSwitch

**当前版本：v1.2.0**（2026-09-13）

一个运行在 Windows 上的网络适配器优先级自动仲裁器。它枚举本机的物理网络适配器（含曾经接入过的），让你为它们设置优先级；开启自动仲裁后，程序会始终**启用优先级最高的可用适配器，并禁用优先级较低的在线适配器**，确保网络流量走你指定的网卡。

## 功能

- **枚举网络适配器**：通过 WMI 三通道读取网卡——
  - `Win32_PnPEntity`（`PNPClass = 'Net'`）判定**设备是否存在**；
  - `MSFT_NetAdapter` 读取当前活跃网卡的**真实链路状态**；
  - `Win32_NetworkAdapter` 补充曾经接入过、现已移除的历史网卡。
- **优先级排序**：为每张网卡设定优先级，一键手动切换或全自动仲裁。
- **自动仲裁**：周期性轮询，保证在已管理范围内只有一个「目标激活网卡」处于启用状态。
  - 内置冷却期（3 秒）与「状态观察期」（8 秒）去抖，避免设备插拔 / WMI 状态抖动引起的反复切换。
  - 手动切换绕过冷却，立即生效；并同步状态基线，不会被随后的自动仲裁撤销。
- **安全护栏**：候选为空、无可用网卡、或关闭自动模式时，绝不执行任何启停操作。
- **虚拟网卡默认忽略**：首次发现的虚拟网卡（如 VPN、虚拟交换机）自动标记为忽略，不被仲裁触碰。
- **系统托盘 + 桌面通知**：关闭窗口后驻留后台运行，托盘图标可打开窗口、切换自动模式 / 开机自启 / 静默启动、退出。
- **单实例**：重复启动会唤起已运行的实例窗口，而不是启动第二个进程。
- **开机自启**：通过计划任务实现（`AtLogon` + 最高权限 + 登录后延迟 15 秒），并会自动校验/修复任务定义。
- **静默启动**：启动后不显示主窗口、不抢焦点、不弹 UAC，直接驻留托盘。可在主窗口或托盘菜单中随时切换。

## 状态语义

程序把「设备在不在」与「链路通不通」当作两个**正交**维度，避免把「设备存在但链路未就绪」误显示为「不存在」。

| 设备存在性 | 链路状态 | 界面文案 | 状态点 |
|---|---|---|---|
| 存在 | 已连接 | 已连接 | 绿 |
| 存在 | 已断开 | 已断开 | 灰 |
| 存在 | 已禁用 | 已禁用 | 红 |
| 存在 | 无法判定 | 状态未知 | 琥珀 |
| **不存在** | — | **不存在**（仅此时使用） | 浅灰 |

- 处于「观察期」的网卡会在文案后追加 `· 观察中`，状态栏同时显示剩余秒数，避免出现「明明已连接，程序却不用它」的困惑。
- 仲裁只把「设备存在 **且** 链路为已连接/已禁用」的网卡视为候选目标；`状态未知` 与 `不存在` 一律不参与，但**不会**因此去禁用其它网卡。

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
- 状态映射规则（`AdapterStateMapper`）与 PnP 存在性集合（`PnpPresenceSet`）是纯函数，脱离真实硬件即可单元测试。
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

### 开机自启与静默启动

- 勾选「开机自启」后，程序会创建计划任务 `NetSwitchAutoStart`：

  ```
  schtasks /create /tn "NetSwitchAutoStart" /tr "\"<exe路径>\" --silent" /sc onlogon /rl highest /delay 0000:15 /f
  ```

  - `/rl highest`：任务以最高权限运行，登录时不会弹 UAC。
  - `/delay 0000:15`：登录后延迟 15 秒启动，等网络栈 / WMI 就绪，避免开机瞬间枚举为空导致误判。
- 「静默启动」控制**双击 exe** 时的行为：勾选则直接驻留托盘，不勾选则显示主窗口。计划任务始终带 `--silent`，因此自启场景总是静默。
- 启动参数优先级：`--silent` / `--show`（命令行） > `StartSilently`（配置项）。
- 程序启动时会解析任务 XML 校验 exe 路径、`--silent` 参数与 `HighestAvailable` 运行级别；任一不符则自动重建任务（覆盖 exe 被移动/升级、参数变更等场景）。

## 许可证

[MIT](LICENSE)
