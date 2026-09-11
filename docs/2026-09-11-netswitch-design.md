# NetSwitch 网络适配器切换器 — 设计规格

- 日期：2026-09-11
- 状态：待审阅
- 项目位置：`D:\NetSwitch`

## 1. 概述

NetSwitch 是一个 Windows 桌面工具，用于**记录本机出现过的网络适配器（网卡），并让机主自定义这些适配器的优先级**。软件在自动模式下按优先级仲裁——启用最高优先级的可用网卡、禁用更低优先级的网卡；当高优先级网卡被拔出时自动递补，重新插入时自动回切。

它是对现有 `C:\Users\FH\network-switcher` 脚本（一对「触发网卡→目标网卡」硬编码切换）的泛化与产品化：从"固定一对"升级为"多网卡优先级排序 + 自动仲裁"。

## 2. 背景与动机

现有 `switcher.ps1` 存在以下局限，本设计逐一解决：

| 现状问题 | 本设计对策 |
|---|---|
| 网卡名、间隔硬编码在脚本里 | 图形化配置 + JSON 持久化 |
| 只支持一对固定切换逻辑 | N 个网卡的优先级排序 + 通用仲裁 |
| 无界面，纯后台脚本 | WPF 托盘 + 主窗口 |
| 日志无限增长 | 轮转 + 自动清理 |
| 开机自启需手动跑 bat | 图形化一键开关 |
| 无法手动干预 | 手动一键切换 + 自动模式开关 |
| 初始 `$prevExists=$true` 存在误判 | 启动时先探测真实状态 |

## 3. 设计目标与成功标准

1. 机主无需改代码即可配置网卡优先级。
2. 自动模式下，任意时刻只有一个「目标激活网卡」处于启用/Up 状态（在已管理范围内）。
3. 拔插网卡时能自动递补与回切，切换过程有日志与桌面通知。
4. 绝不误禁：忽略列表与未知网卡永不被触碰；无任何已知网卡存在时不执行任何禁用。
5. 软件以单文件 exe 交付，双击即用，开机可自启。

## 4. 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 语言/框架 | C# + WPF + .NET 10 | 原生 Windows 桌面，托盘/自启/UAC 处理最顺 |
| MVVM | CommunityToolkit.Mvvm | 轻量、源生成器减少样板 |
| 网卡操作 | Microsoft.Management.Infrastructure (CIM) | 原生 API，枚举/启停网卡，无进程开销 |
| 托盘 | H.NotifyIcon | WPF 下稳定的系统托盘库 |
| 配置持久化 | JSON（`%APPDATA%\NetSwitch\`） | 人类可读、易调试 |
| 日志 | 自定义轮转日志 | 依赖最少 |
| 测试 | xUnit | 核心仲裁逻辑纯单元测试 |
| 打包 | `dotnet publish` 单文件自包含 exe | 用户无需装 .NET 运行时 |

## 5. 系统架构（分层）

```
NetSwitch.sln
├── NetSwitch.Core            # 领域层：仲裁状态机、模型、接口（无外部依赖）
│   ├── Models/               # Adapter, KnownAdapter, AdapterState, Priority
│   ├── Arbitration/          # ArbitrationService（核心仲裁逻辑）
│   └── Abstractions/         # IAdapterRepository, IConfigStore, ILogger, INotifier
├── NetSwitch.Infrastructure  # 基础设施层
│   ├── Cim/                  # CimAdapterRepository（枚举/启用/禁用网卡）
│   ├── Storage/              # JsonConfigStore（配置 + 已知库读写）
│   └── Logging/              # LogService（轮转日志）
├── NetSwitch.App             # 表现层（WPF）
│   ├── Views/                # MainWindow, SettingsView, LogView
│   ├── ViewModels/           # MainViewModel, AdapterItemViewModel 等
│   ├── Tray/                 # 托盘图标 + 上下文菜单
│   └── Monitor/              # 定时轮询，驱动 ArbitrationService
└── NetSwitch.Tests           # xUnit 单元测试
```

依赖方向：`App → Infrastructure → Core`，`Core` 不依赖任何上层或外部库，保证仲裁逻辑可独立测试。

## 6. 核心领域模型

```
AdapterState: Up | Disconnected | Disabled | NotPresent

Adapter（当前系统快照）:
  - Guid: string            # 网卡全局唯一标识
  - Name: string            # 接口名，如 "以太网"
  - InterfaceDescription    # 如 "Realtek PCIe GbE Family Controller"
  - InterfaceType           # Ethernet | WiFi | Virtual | Other
  - State: AdapterState

KnownAdapter（已知库持久化条目）:
  - Guid: string
  - Name / Description / InterfaceType（最近一次快照）
  - Priority: int           # 越小越优先，用户排序决定
  - IsIgnored: bool         # true 则不纳入仲裁
  - FirstSeenAt / LastSeenAt: DateTime
```

`IAdapterRepository`（抽象）：
- `IReadOnlyList<Adapter> GetPresentAdapters()`
- `void Enable(Adapter)` / `void Disable(Adapter)`

仲裁逻辑只依赖此接口，测试时用假实现注入。

## 7. 自动仲裁算法（状态机）

每个轮询周期（默认 2 秒，可配置）执行一次：

1. `present = GetPresentAdapters()` 获取当前存在的网卡。
2. `candidates = KnownAdapters.Where(!IsIgnored && present.Contains(guid))`，按 `Priority` 升序。
3. 若 `candidates` 为空 → **不执行任何操作**（安全：绝不禁用唯一网卡），返回。
4. `available = candidates.Where(State ∈ {Up, Disabled})`，即「有联网潜力」的候选。
5. 若 `available` 为空（全部是 `Disconnected`，即网线未插/未连 WiFi）→ **不执行任何操作**（避免禁用其它网卡造成断网），返回。
6. `target = available.First()`（可用候选中优先级最高者）。
7. 若 `target.State == Disabled` → `Enable(target)`，记日志 + 通知。
8. 对 `candidates` 中优先级低于 `target` 且 `State == Up` 的每个 `adapter` → `Disable(adapter)`，记日志 + 通知。
   - 优先级更低但 `Disconnected` 的网卡不动作（它未在用，无冲突）。
9. 更新各网卡 `LastSeenAt`；首次出现则写入已知库并 `FirstSeenAt`。

**状态迁移语义**（对现有脚本逻辑的修正）：

| 场景 | 行为 |
|---|---|
| 高优先级网卡重新出现且可用（插入、Up/Disabled） | 启用它，禁用更低优先级在线网卡（回切） |
| 高优先级网卡消失（拔出） | 下一轮 target 变为次高优先级，自动启用（递补） |
| 高优先级网卡存在但 `Disconnected`（网线未插/未连 WiFi） | 不动作，低优先级网卡保持 Up（可用性优先，不主动断网） |
| 无任何已知网卡存在 | 不执行任何操作 |
| 所有已管理网卡均 `Disconnected` | 不执行任何操作 |

**防抖**：某网卡在 `Enable`/`Disable` 操作后进入冷却（默认 3 秒），冷却期内不对其重复操作，避免抖动。

**自动模式开关**：关闭时仲裁逻辑不执行 `Enable/Disable`，仅刷新状态展示与记录，切换由用户手动触发。

## 8. 数据持久化

配置文件 `%APPDATA%\NetSwitch\config.json`：

```json
{
  "autoArbitrate": true,
  "pollIntervalSeconds": 2,
  "autoStartEnabled": false,
  "notificationsEnabled": true,
  "knownAdapters": [
    {
      "guid": "…",
      "name": "以太网",
      "description": "Realtek PCIe GbE Family Controller",
      "interfaceType": "Ethernet",
      "priority": 1,
      "isIgnored": false,
      "firstSeenAt": "2026-09-11T12:00:00",
      "lastSeenAt": "2026-09-11T12:00:00"
    }
  ]
}
```

- 写入采用「临时文件 + 原子替换」，避免损坏。
- 读取时若文件损坏或字段缺失，回退默认值并备份损坏文件。

## 9. 功能清单

### V1（本次交付）

1. 自动发现并记录本机网卡（首次/最后见到时间）。
2. 已知适配器优先级排序（主窗口上移/下移）。
3. 自动仲裁开关（自动模式 / 仅记录手动模式）。
4. 手动一键切换到指定网卡。
5. 实时状态显示：Up=绿 / Disconnected=灰 / Disabled=红 / NotPresent=虚。
6. 忽略列表（标记某网卡不纳入仲裁，默认建议忽略虚拟网卡）。
7. 开机自启开关（图形化创建/删除计划任务）。
8. 桌面 toast 通知（发生切换时）。
9. 日志查看窗口 + 轮转清理（保留最近 N 个文件、总大小上限）。

### V2（暂缓，不在本次实现）

- WiFi 热点（SSID）优先级。
- 事件驱动检测（替代轮询）。
- Inno Setup 安装包。
- 多组规则/场景预设。

## 10. 权限与开机自启

- `Enable/Disable-NetAdapter` 需要管理员权限。程序 `app.manifest` 声明 `requireAdministrator`，启动即触发 UAC。
- 开机自启沿用计划任务方案（`AtLogon` + `RunLevel Highest`），由 GUI 一键创建/删除，不再手动执行 bat。
- 创建/删除计划任务本身也需管理员权限，与程序提权一致。

## 11. 错误处理

| 错误 | 处理 |
|---|---|
| 网卡名/描述变化或已不存在 | 以 GUID 匹配，GUID 失配则标记 NotPresent，不崩溃 |
| 权限不足 | 提示以管理员运行，禁用相关操作按钮 |
| 配置文件损坏 | 备份损坏文件，回退默认值 |
| CIM 调用失败 | 记日志，本轮跳过，不中断监控循环 |

## 12. 测试计划

核心仲裁逻辑（`ArbitrationService`）用 xUnit 做纯单元测试，注入假 `IAdapterRepository` 与假 `IConfigStore`，覆盖：

- 高优先级网卡插入 → 禁用低优先级在线网卡（回切）。
- 高优先级网卡拔出 → 启用次高优先级（递补）。
- 忽略列表网卡永不被触碰。
- 无任何已知网卡存在 → 不执行任何操作。
- 自动模式关闭 → 不执行 Enable/Disable。
- 防抖冷却期内不重复操作。
- 首次发现网卡自动入库并记录时间。

CIM 真实网卡操作依赖实际硬件与管理员权限，仅在交付时做手动冒烟验证，不纳入自动化测试。

## 13. 打包与分发

```bash
dotnet publish NetSwitch.App -r win-x64 -c Release \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

产物为单个 exe，双击即用；首次运行触发 UAC。

## 14. 非目标与边界

- 不修改 Windows 路由表或接口跃点（metric），只做网卡启停。
- 不管理 VPN/虚拟网卡（默认建议忽略，用户可手动纳入）。
- 不提供跨平台支持（仅 Windows 10/11）。

## 15. 风险与开放问题

- **禁用唯一网卡导致断网**：由四重护栏覆盖——「candidates 为空不操作」「available 为空不操作（全部 Disconnected）」「忽略列表」「自动模式开关」。若机主把所有已管理网卡都标记忽略、或仅剩的已知网卡全部 Disconnected，软件均不主动禁用，不会主动断网。
- **虚拟网卡误判**：默认建议忽略 `InterfaceType == Virtual` 的网卡，首次识别时提示用户确认。
