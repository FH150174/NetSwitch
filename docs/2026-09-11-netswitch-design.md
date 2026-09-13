# NetSwitch 网络适配器切换器 — 设计规格

- 文档版本：**V1.2.1**
- 日期：V1.0 2026-09-11 / V1.1 2026-09-13 / V1.2 2026-09-13 / **V1.2.1 2026-09-13**
- 状态：**已实现，并通过真机验证**
- 项目位置：`D:\NetSwitch`
- 代码基线：tag **`v1.2.1`**；运行产物 `NetSwitch.App\bin\Release\net10.0-windows\NetSwitch.App.exe`

## 修订记录

| 版本 | 日期 | 变更摘要 |
|---|---|---|
| V1.0 | 2026-09-11 | 初稿：多网卡优先级排序 + 自动仲裁 + 托盘 GUI + 开机自启 |
| V1.1 | 2026-09-13 | ① 新增 §7.5「缺陷修复规格」，修复「优先级 1 的以太网 3 已连接却显示为『不存在』」（D1/D1b/D2/D3/D4/D5）；② 新增 §10.3「静默启动」，开机自启升级为静默启动并新增「静默启动」开关；③ 领域模型引入 `Presence` / `LinkState` 正交维度，废弃单一 `AdapterState` 兼任存在性；④ 新增 §16 实测证据、§17 文件级实施清单 |
| V1.2 | 2026-09-13 | ① **§17 清单 17 项全部落地**（Core / Infrastructure / App / Tests 四层，含新增 `AdapterStateMapper`、`PnpPresenceSet`、`StartupOptions`）；② 25 个单元测试全绿（新增 T1~T8 + S8）；③ **真机验证**：D1 在真实 RNDIS 设备上确认修复、任务自校验自动重建、幽灵条目存量清理、静默启动与单实例唤起均通过（证据见 §18）；④ 追加修复：诊断日志按设备去重（原每轮询周期刷屏）、`JsonConfigStore` 的 AppData 解析加 `USERPROFILE` 兜底；⑤ 版本号定为 `1.2.0` |
| V1.2.1 | 2026-09-13 | **定稿 §15 的开放问题（采纳方案 2）**：`AppConfig.StartSilently` 改为 `bool?`，配置文件无该字段（首次运行 / 从旧版升级）时取 `FirstRunStartSilently = false`（**首次双击给窗口**），并物化落盘；此后完全以用户选择为准。开机自启不受影响（任务固定带 `--silent`）。新增 `StartSilentlyTests`（7 个用例），单元测试总数 32 |

> **V1.1 的两条硬约束（本次交付必须满足）**
> 1. 优先级为 1 的网卡在**物理存在**时，状态绝不允许显示为「不存在」。
> 2. **保留**开机自启能力，并将其**升级为静默启动**（登录后不弹窗、不抢焦点、直接驻留托盘），同时提供一个用户可见的「静默启动」功能开关。
>
> **V1.2 验收结论**：两条均已满足，并在真机（`以太网 3`，USB RNDIS 设备）上取得正向证据，见 §18。

---

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
| 开机自启需手动跑 bat | 图形化一键开关（计划任务） |
| **开机自启会弹出窗口、打断登录** | **V1.1：自启默认静默，可开关（§10.3）** |
| 无法手动干预 | 手动一键切换 + 自动模式开关 |
| 初始 `$prevExists=$true` 存在误判 | 启动时先探测真实状态 |
| **USB/RNDIS 网卡已连接却被判为「不存在」** | **V1.1：存在性与可用性解耦（§7.5）** |

## 3. 设计目标与成功标准

1. 机主无需改代码即可配置网卡优先级。
2. 自动模式下，任意时刻只有一个「目标激活网卡」处于启用/Up 状态（在已管理范围内）。
3. 拔插网卡时能自动递补与回切，切换过程有日志与桌面通知。
4. 绝不误禁：忽略列表与未知网卡永不被触碰；无任何已知网卡存在时不执行任何禁用。
5. 软件以单文件 exe 交付，双击即用，开机可自启。
6. **（V1.1）开机自启必须静默**：登录后不显示主窗口、不抢焦点、不弹 UAC、不闪控制台窗口；用户可从托盘随时打开界面。
7. **（V1.1）状态显示必须诚实**：只要设备节点在系统中存在，状态就不得显示为「不存在」；存在性与链路可用性必须分别呈现。

## 4. 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 语言/框架 | C# + WPF + .NET 10（`net10.0-windows`） | 原生 Windows 桌面，托盘/自启/UAC 处理最顺 |
| MVVM | CommunityToolkit.Mvvm | 轻量、源生成器减少样板 |
| 网卡操作 | `System.Management`（WMI） | 原生 API，枚举/启停网卡，无进程开销 |
| 托盘 | H.NotifyIcon.Wpf | WPF 下稳定的系统托盘库 |
| 配置持久化 | JSON（`%APPDATA%\NetSwitch\`） | 人类可读、易调试 |
| 日志 | 自定义轮转日志 | 依赖最少 |
| 测试 | xUnit | 核心仲裁逻辑纯单元测试 |
| 打包 | `dotnet publish` 单文件自包含 exe | 用户无需装 .NET 运行时 |

**WMI 类分工（V1.1 明确）**

| 类 | 命名空间 | 用途 | 关键属性 |
|---|---|---|---|
| `MSFT_NetAdapter` | `root\StandardCimv2` | **活跃通道**：当前在 IP 栈中存在的接口 | `InterfaceGuid`、`Name`、`InterfaceDescription`、`InterfaceOperationalStatus`、`InterfaceAdminStatus`、`MediaConnectState`、`Virtual`、`InterfaceType`、`PnpDeviceID` |
| `Win32_NetworkAdapter` | `root\CIMv2` | **历史通道**：含"曾接入过、现已不存在"的设备 | `GUID`、`NetConnectionID`、`Name`、`PNPDeviceID`、`AdapterType` |
| `Win32_PnPEntity` | `root\CIMv2` | **存在性判定（V1.1 新增）**：设备节点是否真的在系统里 | `DeviceID`（对应 `PnpDeviceID`）、`PNPClass='Net'`、`Status`、`ConfigManagerErrorCode` |

> 不用 `Microsoft.Management.Infrastructure`：其 2.0.0 在 .NET 8+ RID 图下存在运行时缺件问题，故统一走 `System.Management`。

## 5. 系统架构（分层）

```
NetSwitch.sln
├── NetSwitch.Core            # 领域层：仲裁状态机、模型、接口（无外部依赖）
│   ├── Models/               # Adapter, KnownAdapter, AdapterState, AdapterPresence, Priority
│   ├── Arbitration/          # ArbitrationService（核心仲裁逻辑）
│   └── Abstractions/         # IAdapterRepository, IConfigStore, ILogger, INotifier
├── NetSwitch.Infrastructure  # 基础设施层
│   ├── Cim/                  # CimAdapterRepository（活跃/历史/存在性三通道枚举 + 启停）
│   ├── Storage/              # JsonConfigStore（配置 + 已知库读写）
│   └── Logging/              # LogService（轮转日志）
├── NetSwitch.App             # 表现层（WPF）
│   ├── Views/                # MainWindow, LogView
│   ├── ViewModels/           # MainViewModel, AdapterItemViewModel 等
│   ├── Services/             # TrayIconService, AutoStartService, AdapterMonitor, StartupOptions
│   ├── Tray/                 # 托盘图标 + 上下文菜单
│   └── Monitor/              # 定时轮询，驱动 ArbitrationService
└── NetSwitch.Tests           # xUnit 单元测试
```

依赖方向：`App → Infrastructure → Core`，`Core` 不依赖任何上层或外部库，保证仲裁逻辑可独立测试。

**V1.1 新增/调整的组件**

| 组件 | 职责 |
|---|---|
| `StartupOptions`（App/Services） | 解析命令行 `--silent` / `--show`，与 `AppConfig.StartSilently` 合并出最终启动模式 |
| `AutoStartService`（改造） | 计划任务命令行带 `--silent`；`IsEnabled()` 升级为「校验任务是否指向当前 exe 且带静默参数」，不一致则修复 |
| `AdapterItemViewModel`（改造） | 名称/描述/类型改为可刷新；新增 `Presence`、`IsInGracePeriod` 展示维度 |

## 6. 核心领域模型

### 6.1 V1.1 修正：存在性与可用性正交

V1.0 用单一 `AdapterState` 同时表达「设备在不在」和「链路通不通」，这是 D1 缺陷的根源。V1.1 拆成两个正交维度：

```
AdapterPresence（设备是否存在）
  Present   # 设备节点在系统中（Win32_PnPEntity 命中，或活跃通道命中）
  Absent    # 设备节点确实不在（仅历史通道有记录）

AdapterLinkState（链路可用性，仅在 Present 时有意义）
  Up            # 已启用且链路已连接
  Disconnected  # 已启用但链路未就绪（网线未插 / 未连 WiFi / 媒体断开）
  Disabled      # 被管理员禁用
  Unknown       # 无法判定（属性缺失或驱动未上报）

Adapter（当前系统快照）:
  - Guid: string                 # 接口 GUID（NetCfgInstanceId），唯一标识
  - Name: string                 # 接口别名，如 "以太网 3"
  - InterfaceDescription: string
  - InterfaceType: Ethernet | WiFi | Virtual | Other
  - Presence: AdapterPresence    # V1.1
  - LinkState: AdapterLinkState  # V1.1
  - PnpDeviceId: string?         # V1.1，用于存在性核对
  - Raw: string?                 # V1.1，原始诊断串（oper/admin/media），仅用于日志

KnownAdapter（已知库持久化条目）:
  - Guid: string
  - Name / Description / InterfaceType（最近一次快照）
  - Priority: int                # 越小越优先，用户排序决定
  - IsIgnored: bool              # true 则不纳入仲裁
  - FirstSeenAt / LastSeenAt: DateTime
```

**兼容性**：`AdapterState` 保留为**展示层聚合视图**（`Presence==Absent → NotPresent`；否则取 `LinkState`），用于最小化 UI 改动；但**仲裁逻辑必须直接读 `Presence` + `LinkState`**，不得再依赖聚合值。

`IAdapterRepository`（抽象，V1.1 微调）：

- `IReadOnlyList<Adapter> GetPresentAdapters()`
- `void Enable(Adapter)` / `void Disable(Adapter)`
- `bool IsDevicePresent(string pnpDeviceId)`（V1.1 新增，默认实现可由仓库内部批量预取优化）

## 7. 自动仲裁算法（状态机）

每个轮询周期（默认 2 秒，可配置）执行一次：

1. `present = GetPresentAdapters()` 获取当前快照（V1.1：快照内 GUID 必须唯一，见 D4）。
2. `candidates = KnownAdapters.Where(!IsIgnored && present.Contains(guid))`，按 `Priority` 升序。
3. 若 `candidates` 为空 → **不执行任何操作**（安全：绝不禁用唯一网卡），返回。
4. `available = candidates.Where(Presence == Present && LinkState ∈ {Up, Disabled})`，即「有联网潜力」的候选。**（V1.1 修正：`Absent` 与 `Unknown` 一律排除，`Disconnected` 仍排除。）**
5. 若 `available` 为空（全部是 `Disconnected`，即网线未插/未连 WiFi）→ **不执行任何操作**（避免禁用其它网卡造成断网），返回。
6. `target = available.First()`（可用候选中优先级最高者）。
7. 若 `target.LinkState == Disabled` → `Enable(target)`，记日志 + 通知。
8. 对 `candidates` 中优先级低于 `target` 且 `LinkState == Up` 的每个 `adapter` → `Disable(adapter)`，记日志 + 通知。
   - 优先级更低但 `Disconnected` 的网卡不动作（它未在用，无冲突）。
9. 更新各网卡 `LastSeenAt`；首次出现则写入已知库并 `FirstSeenAt`。

**状态迁移语义**

| 场景 | 行为 |
|---|---|
| 高优先级网卡重新出现且可用（插入、Up/Disabled） | 启用它，禁用更低优先级在线网卡（回切） |
| 高优先级网卡消失（拔出） | 下一轮 target 变为次高优先级，自动启用（递补） |
| 高优先级网卡存在但 `Disconnected`（网线未插/未连 WiFi） | 不动作，低优先级网卡保持 Up（可用性优先，不主动断网） |
| **高优先级网卡物理存在、但链路状态暂时无法判定（`Unknown`）** | **（V1.1）不动作，且不得把它显示为「不存在」；UI 标注「状态未知」** |
| 无任何已知网卡存在 | 不执行任何操作 |
| 所有已管理网卡均 `Disconnected` | 不执行任何操作 |

**防抖**：某网卡在 `Enable`/`Disable` 操作后进入冷却（默认 3 秒），冷却期内不对其重复操作，避免抖动。

**观察期（V1.1 语义收紧）**：某网卡「由不可用变为可用」后进入观察期（默认 8 秒），观察期内不把它当作仲裁目标，避免 WMI 状态抖动引起的反复切换。V1.1 明确：

- 观察期**只影响「能否成为 target」**，不影响「是否存在」——观察期内 UI 必须显示真实链路状态（`Up`/`Disabled`），并额外标注「观察中」。
- 网卡离开 `Up/Disabled` 时，必须清除其观察期时间戳（`_becameAvailableAt` 移除该项），否则下次可用时会带着旧时间戳被误判为「已过观察期」。

**自动模式开关**：关闭时仲裁逻辑不执行 `Enable/Disable`，仅刷新状态展示与记录，切换由用户手动触发。

## 7.5 缺陷修复规格（V1.1 核心）

### D1（主缺陷）：优先级 1 的「以太网 3」已连接却显示为「不存在」

**现象**
机主把 USB 网络共享设备（`Remote NDIS based Internet Sharing Device #2`，接口别名「以太网 3」）设为优先级 1。该设备已插上、`netsh` 显示「已启用 / 已连接」时，主窗口仍显示状态「不存在」；且自动仲裁从不选它——日志里始终去启用/禁用优先级 2 的「以太网」，从未出现过任何关于「以太网 3」的启停记录。

**根因（代码定位）**

| # | 位置 | 问题 |
|---|---|---|
| 1 | `CimAdapterRepository.EnumerateHistoricalAdapters()`（历史通道） | 把历史网卡状态**硬编码**为 `AdapterState.NotPresent`，无任何"是否真的不在"的校验 |
| 2 | 同上，去重判据 | 历史条目是否跳过，**仅**取决于「活跃通道此刻是否命中同一 GUID」。只要活跃通道在某一时刻没枚举到该设备（驱动尚未完成加载、USB 选择性挂起、接口短暂消失、驱动未上报 `InterfaceGuid` 等），同一张**物理存在**的网卡就会以 `NotPresent` 混入快照 |
| 3 | `MainViewModel.Refresh()` | 用 GUID 在快照里查状态；查不到或状态为 `NotPresent` → `AdapterItemViewModel.StateText` 渲染成「不存在」 |
| 4 | `ArbitrationService.Arbitrate()` 的 `available` 判据 | 只接受 `State ∈ {Up, Disabled}`，`NotPresent` 被直接排除 → 优先级 1 的网卡**永远进不了 target**，于是去仲裁优先级 2 的网卡 |
| 5 | `CimAdapterRepository.MapActiveState()` | `InterfaceAdminStatus == 2`（管理员禁用）只在 `oper == 2` 分支里被检查；`oper == 6` 被无条件判为 `NotPresent`。RNDIS/USB 类驱动在"已禁用"或"链路未就绪"时会上报 `oper == 6`，于是被误判为"设备不存在" |

**实测证据**（完整数据见 §16）：同一设备在活跃通道与历史通道中 GUID 完全一致（`{EC0DFDE6-…}`），说明是同一实体；配置里同时存在以该 GUID 为键的「以太网 3」和以设备名为键的 `Remote NDIS based Internet Sharing Device`（后者是历史通道产出的"已不存在"幽灵条目）；日志显示该网卡在 `00:08:21` 仍是仲裁目标（当时可用），`00:45:29` 已不是，`00:45:37` 又恢复（恰好 8 秒观察期后）——即该设备在"可见/不可见"之间反复，而 UI 在不可见窗口期显示「不存在」。

**修复规格**

1. **存在性判定改为以 PnP 设备节点为准**（不再依赖"活跃通道是否命中"）：
   - 每轮轮询额外枚举一次 `Win32_PnPEntity WHERE PNPClass = 'Net'`，建立 **大小写不敏感**的 `DeviceID` 集合（注意：实测同一设备的 `PnpDeviceID` 来自 `MSFT_NetAdapter` 时为小写 `…&0&0000`，来自 `Win32_PnPEntity` 时为大写 `…&0&0000`，必须忽略大小写）。
   - 活跃通道命中的网卡 → `Presence = Present`。
   - 仅历史通道命中的网卡：`DeviceID` 命中集合 → `Presence = Present`（且 `LinkState` 依 `Win32_NetworkAdapter.NetConnectionStatus`/`NetEnabled` 推断，推不出则 `Unknown`）；未命中 → `Presence = Absent`。
2. **`MapActiveState()` 重写判定优先级**（D1b）：

   | 条件（按序判断） | 结果 |
   |---|---|
   | `InterfaceAdminStatus == 2` | `Disabled`（**最高优先**，先于一切） |
   | `InterfaceOperationalStatus == 1 && MediaConnectState ∈ {缺失, 1}` | `Up` |
   | `InterfaceOperationalStatus ∈ {2, 5, 7}`，或 `== 1 && MediaConnectState ∉ {缺失, 1}` | `Disconnected` |
   | `InterfaceOperationalStatus == 6` 且设备节点存在 | `Disconnected`（**不得判为不存在**） |
   | `InterfaceOperationalStatus == 6` 且设备节点不存在 | `Absent` |
   | 属性缺失 / 无法判定 | `Unknown` |

   > 实现细节：`MediaConnectState` 缺失时不视为与 `oper == 1` 矛盾，直接按 `oper` 判 `Up`。
   > 若严格按「`media != 1` 即 `Disconnected`」，驱动未上报媒体状态的网卡会被降级为 `Disconnected`
   > 从而永远进不了 `available`——这会以另一种形式重现 D1 的「从不被选中」。
   > 该规则实现在 `NetSwitch.Infrastructure/Cim/AdapterStateMapper.MapActive()`，为纯函数，可单元测试。

3. **快照合并规则**：同一 GUID 在活跃通道与历史通道同时出现时，**活跃通道优先**，历史条目必须丢弃；快照内 GUID 必须唯一。
4. **UI 文案**（`AdapterItemViewModel.StateText`）：

   | 状态 | 文案 | 颜色点 |
   |---|---|---|
   | `Present` + `Up` | 已连接 | 绿 |
   | `Present` + `Disconnected` | 已断开 | 灰 |
   | `Present` + `Disabled` | 已禁用 | 红 |
   | `Present` + `Unknown` | **状态未知**（V1.1 新增） | 琥珀 |
   | `Absent` | 不存在（**仅此时使用**） | 浅灰 |
   | 观察期内 | 追加「· 观察中」 | 沿用链路色 |

5. **仲裁**：`available = Presence == Present && LinkState ∈ {Up, Disabled}`；`Unknown`/`Absent` 不参与 target 选择，但**不改变**既有安全护栏（不得因此主动禁用其它网卡）。
6. **诊断日志**：任一网卡的 `Presence`/`LinkState` 发生变化时，必须记录一行结构化日志，便于复现与回归：
   `网卡状态变更 guid=… name=… presence=… link=… oper=… admin=… media=… pnp=…`

### D2：观察期内「已连接却不切换」缺少可见性

`AvailabilityGracePeriod`（8 秒）期间网卡被排除出 target，但 UI 无任何提示，机主看到的是「明明已连接，程序却不用它」。**规格**：观察期内在列表项与状态栏标注「观察中（剩余 N 秒）」；观察期只影响 target 选择，不影响状态显示与存在性（见 §7）。

### D3：列表项的名称/描述/类型构造后永不刷新

`AdapterItemViewModel` 的 `Name` / `Description` / `InterfaceType` 是只读属性，仅在构造时从 `KnownAdapter` 取值；`UpdateFrom()` 只更新 `State` / `IsPresent`。网卡改名、换描述、类型变化后，UI 会长期显示旧值。**规格**：三者改为可观察属性并在 `UpdateFrom()` 中同步；名称发生变化时写一条 INFO 日志。

### D4：快照出现重复 GUID 会导致整个列表刷新失败

`MainViewModel.Refresh()` 用 `GetPresentAdapters().ToDictionary(a => a.Guid, …)` 建索引，一旦快照中出现重复 GUID 就抛 `ArgumentException`，被 `catch` 吞掉后**整个列表停止更新**（用户看到的是长期冻结的旧状态）。**规格**：

- `CimAdapterRepository` 侧保证快照内 GUID 唯一（见 §7.5 修复规格第 3 条）。
- `MainViewModel` 侧改为容错建表（`TryAdd` 跳过重复项并记 WARN），并把「刷新失败」明确写进状态栏与日志。

### D5：历史通道的稳定标识与幽灵条目

历史通道在 GUID 缺失时依次退化为 `PNPDeviceID` → 设备名作为键。实测已产生以设备名 `Remote NDIS based Internet Sharing Device` 为键、描述为空的幽灵条目。**规格**：历史通道建键优先级固定为 `GUID` → `PNPDeviceID`；两者皆缺失时**不登记**（避免用设备名造键产生不可合并的重复项），并在该情况下写 WARN 日志。

**存量数据迁移**：V1.0 已经写进 `config.json` 的幽灵条目不会自动消失。`ArbitrationService.UpdateKnownAdapters()` 每轮会清理「键既不是 GUID、也不含 `\`（PnP 设备 ID 必含 `\`）」的条目并写 WARN——这类键 V1.1 的仓库已不可能再生产，因此判定为不可再生的遗留数据。清理后 UI 中对应的「不存在」行也会同步移除。

---

## 8. 数据持久化

配置文件 `%APPDATA%\NetSwitch\config.json`：

```json
{
  "autoArbitrate": true,
  "pollIntervalSeconds": 2,
  "autoStartEnabled": false,
  "startSilently": true,
  "notificationsEnabled": true,
  "knownAdapters": [
    {
      "guid": "{EC0DFDE6-17AB-4AB1-8756-ADCA3F917A10}",
      "name": "以太网 3",
      "description": "Remote NDIS based Internet Sharing Device #2",
      "interfaceType": "Ethernet",
      "priority": 1,
      "isIgnored": false,
      "firstSeenAt": "2026-09-12T00:23:26",
      "lastSeenAt": "2026-09-13T00:46:15"
    }
  ]
}
```

- **V1.1 新增字段**：`startSilently`（bool，默认 `true`）——见 §10.3。
- 向后兼容：旧配置文件缺 `startSilently` 时，反序列化取默认值 `true`（即"升级为静默启动"对存量用户同样生效）。
- 写入采用「临时文件 + 原子替换」，避免损坏。
- 读取时若文件损坏或字段缺失，回退默认值并备份损坏文件；IO 瞬时错误返回内存缓存，避免用空配置覆盖用户数据。

## 9. 功能清单

### V1.1（本次交付）

1. 自动发现并记录本机网卡（首次/最后见到时间）。
2. 已知适配器优先级排序（主窗口上移/下移）。
3. 自动仲裁开关（自动模式 / 仅记录手动模式）。
4. 手动一键切换到指定网卡。
5. 实时状态显示：已连接=绿 / 已断开=灰 / 已禁用=红 / **状态未知=琥珀（新增）** / 不存在=虚（**仅在设备节点确实缺失时出现**）。
6. 忽略列表（标记某网卡不纳入仲裁，默认建议忽略虚拟网卡）。
7. 开机自启开关（图形化创建/删除计划任务）。
8. **静默启动**：自启时不显示主窗口、不抢焦点、不弹 UAC，直接驻留托盘。
9. **静默启动开关**：主窗口工具栏复选框 + 托盘右键勾选项，可随时切换。
10. **观察期可见性**：网卡处于状态观察期时在列表与状态栏标注「观察中」。
11. **状态变更诊断日志**：记录 `presence/link/oper/admin/media/pnp`，便于定位 USB 网卡类问题。
12. 桌面 toast 通知（发生切换时）。
13. 日志查看窗口 + 轮转清理（保留最近 10 个文件、单文件 512 KB、总大小 5 MB）。

### V2（暂缓，不在本次实现）

- WiFi 热点（SSID）优先级。
- 事件驱动检测（替代轮询，用 `WMI` 事件订阅或 `NotifyAddrChange`）。
- Inno Setup 安装包。
- 多组规则/场景预设。
- 网卡"从列表中删除"（当前已知库只增不减）。

## 10. 权限、开机自启与静默启动

### 10.1 权限

- `Enable/Disable-NetAdapter` 需要管理员权限。程序 `app.manifest` 声明 `requireAdministrator`，**手动启动**时触发 UAC。
- 创建/删除计划任务本身也需管理员权限，与程序提权一致。
- 提权失败（例如用户取消了 UAC）时，网卡启停相关按钮必须禁用并给出明确提示，不得静默失败。

### 10.2 开机自启（保留，方案不变）

- 沿用计划任务方案：`AtLogon` + `RunLevel Highest`，由 GUI 一键创建/删除，不再手动执行 bat。
- 任务名：`NetSwitchAutoStart`。
- **`RunLevel Highest` 是硬性要求**：清单声明了 `requireAdministrator`，若任务未以最高权限运行，登录时会因提权失败而弹 UAC 或直接启动失败，从而破坏静默启动。
- **新增：任务自校验与修复**。`IsEnabled()` 不能只判断"任务是否存在"，必须解析任务定义（`schtasks /query /tn <name> /xml`）并校验：
  1. 可执行文件路径 == 当前 `Environment.ProcessPath`；
  2. 参数中包含 `--silent`；
  3. `RunLevel` 为 `HighestAvailable`。
  任一不满足 → 视为"需要修复"，在用户已开启自启的前提下自动重建任务（覆盖 exe 被移动/升级、参数变更等场景）。

### 10.3 静默启动（V1.1 新增）

**目标**：登录后由计划任务拉起进程时，不显示主窗口、不抢焦点、不弹 UAC、不闪控制台窗口，直接驻留托盘；同时把"是否静默"开放为一个用户可切换的开关。

**配置与命令行**

| 项 | 说明 |
|---|---|
| `AppConfig.StartSilently`（`bool?`） | 用户可见的「静默启动」开关。**`null` = 配置文件中尚无该字段**（首次运行，或从 V1.2.0 之前的版本升级），此时按 `AppConfig.FirstRunStartSilently`（**`false`**）取值，即首次双击给窗口；该值在首次保存时物化落盘，此后完全以用户选择为准（V1.2.1 定稿，见 §15） |
| `--silent`（命令行） | 强制静默；计划任务固定使用 |
| `--show`（命令行） | 强制显示主窗口（用于"这次我要看界面"的场景） |
| 优先级 | 命令行 > 配置项。即 `--silent`/`--show` 存在时忽略 `StartSilently` |

**启动行为矩阵**

| 触发方式 | `StartSilently` | 行为 |
|---|---|---|
| 计划任务（命令行含 `--silent`） | 任意 | **静默**：不 Show 主窗口，仅创建托盘图标 |
| 用户双击 exe | `true` | **静默** |
| 用户双击 exe | `false` | 显示主窗口 |
| 第二次启动 exe（已有实例） | 任意 | 通过命名事件唤起已有实例的主窗口，自身退出 |
| 托盘「打开 NetSwitch」/ 左键单击 | 任意 | 显示主窗口 |
| 托盘「退出」 | 任意 | 结束进程 |

**计划任务命令行（V1.1 定稿）**

```
schtasks /create /tn "NetSwitchAutoStart" /tr "\"<exe路径>\" --silent" /sc onlogon /rl highest /delay 0000:15 /f
```

- `/delay 0000:15`：登录后延迟 15 秒再启动。目的有二：等网络栈/WMI 就绪（避免开机瞬间枚举为空导致误判与抖动）；避开登录瞬间的磁盘/CPU 争抢。
- 任务以登录用户身份在交互会话中运行（不带 `/ru`），因此**不会**弹 UAC。

**启动流程改动（`App.xaml.cs`）**

1. 解析命令行得到 `cliMode`（`--silent` / `--show` / 未指定）。
2. `silent = cliMode ?? config.StartSilently`。
3. 静默时**不调用** `_mainWindow.Show()`；仍创建窗口对象（保持隐藏），以保留单实例唤起与托盘打开能力。
4. `ShutdownMode` 必须为 `OnExplicitShutdown`（当前已满足）——否则无可见窗口时 WPF 会立即退出。
5. 首个仲裁周期前加入短延迟（建议 2~3 秒）或"首次枚举成功后再启动监控"，避免登录早期 WMI 未就绪。
6. 托盘图标必须在启动阶段强制创建（`TaskbarIcon.ForceCreate`，当前已满足）。静默模式下托盘是**唯一入口**，创建失败必须记 ERROR 并在下次可用时重试。
7. 通知必须完全容错（`Notify` 内部 try/catch，当前已满足）。历史日志中出现过 `TrayIcon is not created` 异常逃逸出仲裁、导致整个 `Tick`（含配置保存）失败的案例——静默启动场景下这类异常更容易触发，必须保证"通知失败绝不中断仲裁"。
8. 关闭主窗口 = 隐藏到托盘而非退出（`MainWindow.OnClosing` 拦截，当前已满足）。

**UI**

- 主窗口工具栏：在「开机自启」右侧新增复选框「静默启动」（绑定 `MainViewModel.StartSilently`）。
- 托盘右键菜单：新增「开机自启」与「静默启动」两个勾选项，展开菜单时回读配置保持勾选一致。
- 首次开启「开机自启」时若「静默启动」未勾选，给出提示并默认建议勾选（"升级为静默启动"的默认体验）。

## 11. 错误处理

| 错误 | 处理 |
|---|---|
| 网卡名/描述变化或已不存在 | 以 GUID 匹配；GUID 失配时按 §7.5 规则区分 `Absent`（设备节点确实缺失）与 `Unknown`（状态无法判定），均不崩溃 |
| **活跃通道暂时未命中、但设备节点存在** | **（V1.1）判定为 `Present`，`LinkState` 推不出时取 `Unknown`；UI 显示「状态未知」，绝不显示「不存在」** |
| **快照中出现重复 GUID** | **（V1.1）仓库侧去重（活跃优先）；UI 侧容错建表并记 WARN，不得让整表刷新失败** |
| 权限不足 | 提示以管理员运行，禁用相关操作按钮 |
| 配置文件损坏 | 备份损坏文件，回退默认值 |
| 配置文件被瞬时锁定（IO 错误） | 返回内存缓存，避免用空配置覆盖 |
| CIM 调用失败 | 记日志，本轮跳过，不中断监控循环 |
| **计划任务失效（exe 被移动/参数过期）** | **（V1.1）`IsEnabled()` 校验失败时自动重建任务；重建失败则回滚开关状态并提示** |
| **静默启动时托盘图标创建失败** | **（V1.1）记 ERROR；降级为显示主窗口，保证用户仍有操作入口** |

## 12. 测试计划

核心仲裁逻辑（`ArbitrationService`）用 xUnit 做纯单元测试，注入假 `IAdapterRepository` 与假 `IConfigStore`。

**已有用例（保持）**

- 高优先级网卡插入 → 禁用低优先级在线网卡（回切）。
- 高优先级网卡拔出 → 启用次高优先级（递补）。
- 忽略列表网卡永不被触碰。
- 无任何已知网卡存在 → 不执行任何操作。
- 自动模式关闭 → 不执行 Enable/Disable。
- 防抖冷却期内不重复操作。
- 首次发现网卡自动入库并记录时间。
- 默认忽略虚拟网卡。
- 所有候选均断开 → 不执行任何操作。
- 手动切换 → 启用目标并禁用其它已管理在线网卡。

**V1.1 新增用例**

| # | 用例 | 断言 |
|---|---|---|
| T1 | `Presence == Present` 且 `LinkState == Disconnected` 的优先级 1 网卡 + 优先级 2 网卡 `Up` | 不做任何启停；优先级 1 网卡**不被判为 NotPresent** |
| T2 | `Presence == Absent` 的优先级 1 网卡 + 优先级 2 网卡 `Up` | 不做任何启停 |
| T3 | `LinkState == Unknown` 的优先级 1 网卡 + 优先级 2 网卡 `Up` | 不做任何启停（安全优先），且不产生"已禁用"副作用 |
| T4 | 状态映射：`admin == 2 && oper == 6` | 映射为 `Disabled`，不是 `NotPresent` |
| T5 | 状态映射：`admin == 1 && oper == 6 && media == 1` | 映射为 `Up` 或 `Disconnected`，**绝不是** `Absent` |
| T6 | 快照含重复 GUID | 快照去重为 1 条；UI 建表不抛异常 |
| T7 | 观察期语义 | 观察期内不成为 target；观察期结束后成为 target；离开可用状态后时间戳被清除 |
| T8 | 存在性判定（含大小写差异的 `PnpDeviceID`） | 大小写不同也能命中 `Present` |

**CIM 相关（需真实硬件与管理员权限）**

- 活跃通道与历史通道的 GUID 一致性核对（同一设备不得产生两条记录）。
- `Win32_PnPEntity` 存在性核对：拔掉 USB 网卡后 `Presence` 变为 `Absent`；插上后恢复 `Present`。
- 以上依赖实际硬件，仅做交付前手动冒烟验证，不纳入自动化测试。

**静默启动验证（手动 + 可自动化部分）**

| # | 验证项 | 方法 |
|---|---|---|
| S1 | 计划任务命令行正确 | `schtasks /query /tn NetSwitchAutoStart /xml` 输出含 `--silent` 且 `RunLevel` 为 `HighestAvailable` |
| S2 | 静默启动不弹窗 | 注销后重新登录，观察是否出现主窗口；或用 `--silent` 启动后断言 `Application.Current.Windows` 中无可见窗口 |
| S3 | 静默启动后托盘可用 | 托盘图标存在，左键单击能打开主窗口 |
| S4 | 单实例唤起 | 静默运行中再次双击 exe → 已有实例显示主窗口，不产生第二个进程 |
| S5 | 关闭窗口 = 隐藏 | 点右上角关闭 → 进程仍在，托盘图标仍在 |
| S6 | 非静默模式 | 关闭「静默启动」后双击 exe → 主窗口直接显示 |
| S7 | 任务自校验 | 手动改坏任务参数（去掉 `--silent`）后重启程序 → 程序自动重建任务 |
| S8 | 通知失败不影响仲裁 | 人为让托盘通知失败 → 仲裁周期仍正常完成并保存配置 |

## 13. 打包与分发

```bash
dotnet publish NetSwitch.App -r win-x64 -c Release \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

产物为单个 exe，双击即用；首次运行触发 UAC。**注意**：exe 移动位置后，已创建的计划任务会失效——由 §10.2 的任务自校验与修复逻辑兜底。

## 14. 非目标与边界

- 不修改 Windows 路由表或接口跃点（metric），只做网卡启停。
- 不管理 VPN/虚拟网卡（默认建议忽略，用户可手动纳入）。
- 不提供跨平台支持（仅 Windows 10/11）。
- 不做「已知库条目删除」（V2 再议）。

## 15. 风险与开放问题

- **禁用唯一网卡导致断网**：由四重护栏覆盖——「candidates 为空不操作」「available 为空不操作（全部 Disconnected）」「忽略列表」「自动模式开关」。V1.1 进一步把 `Unknown` 排除出 target，护栏更强。
- **虚拟网卡误判**：默认建议忽略 `InterfaceType == Virtual` 的网卡，首次识别时提示用户确认。
- **USB/RNDIS 网卡状态抖动**（本次 D1 的触发条件）：USB 网络共享设备会因驱动加载时序、USB 选择性挂起等原因在"活跃通道可见/不可见"之间反复。V1.1 通过"存在性以 PnP 节点为准 + `oper==6` 不再判不存在 + 观察期可见化 + 状态变更诊断日志"覆盖；若仍偶发，可考虑 V2 的事件驱动检测。
- **~~开放问题~~ → 已定稿（V1.2.1 采纳方案 2）**：`StartSilently` 的默认值。
  背景：对"双击 exe 就是为了看界面"的用户，默认静默会让人以为程序没启动——V1.2.0 交付时**实测复现了这一困惑**（双击后无窗口，仅托盘图标）。
  **定稿方案**：`AppConfig.StartSilently` 改为 `bool?`；配置文件中**尚无该字段**时（首次运行，或从 V1.2.0 之前升级上来）取 `AppConfig.FirstRunStartSilently = false`，即**首次双击给窗口**；该值在首次保存时物化落盘，此后完全以用户选择为准。
  这样既保留了「自启静默」的默认体验（计划任务固定带 `--silent`，命令行优先级高于配置项），又消除了首次双击时的"程序没启动"误判。回归用例见 `NetSwitch.Tests/StartSilentlyTests.cs`（7 个）。
  未采纳的备选：① 保持默认 `true`，仅靠 UI 复选框与托盘提示引导（仍会误判）；② 默认 `false`，把静默完全交给计划任务与用户显式勾选（削弱了「静默启动」作为默认体验的意图）。

## 16. 附录 A：本次诊断的实测证据

环境：Windows，`D:\NetSwitch`，commit `227c76f`，运行进程 `NetSwitch.App.exe`（PID 10452，构建于 2026-09-12 01:08）。

### A.1 系统侧状态（设备实际是"已启用 + 已连接"）

```
$ netsh interface show interface
管理员状态     状态           类型      接口名称
已启用        已连接         专用      VMware Network Adapter VMnet1
已启用        已连接         专用      VMware Network Adapter VMnet8
已禁用        已断开连接      专用      以太网
已启用        已连接         专用      以太网 3

$ getmac /v /fo list
连接名:     以太网 3
网络适配器: Remote NDIS based Internet Sharing Device #2
物理地址:   F6-FC-CE-D5-0A-F7
传输名称:   \Device\Tcpip_{EC0DFDE6-17AB-4AB1-8756-ADCA3F917A10}
```

### A.2 两条枚举通道对同一设备给出同一 GUID

```
MSFT_NetAdapter（活跃通道）
  Name=以太网 3 | Guid={EC0DFDE6-17AB-4AB1-8756-ADCA3F917A10} | InstanceID={EC0DFDE6-…}
  | Oper=1 | Admin=1 | MediaConn=1 | Virtual=False | InterfaceType=6
  | Pnp=USB\VID_2D95&PID_600A&MI_00\8&324bd37&0&0000
  | Desc=Remote NDIS based Internet Sharing Device #2

Win32_NetworkAdapter（历史通道）
  Name=Remote NDIS based Internet Sharing Device #2 | NetConnectionID=以太网 3
  | GUID={EC0DFDE6-17AB-4AB1-8756-ADCA3F917A10}
  | PNP=USB\VID_2D95&PID_600A&MI_00\8&324BD37&0&0000   ← 注意大小写差异
  | AdapterType=以太网 802.3 | NetEnabled=True | NetConnectionStatus=2

Win32_PnPEntity（PNPClass='Net'）
  Name=Remote NDIS based Internet Sharing Device #2 | Status=OK | ConfigManagerErrorCode=0
  Name=Realtek Gaming 2.5GbE Family Controller | Status=Error | ConfigManagerErrorCode=22  ← 已被禁用
```

结论：两条通道指向**同一实体**，因此"设备是否真的在系统中"这一信息在合并后完全丢失——只要活跃通道没命中，该设备就会退化为"已不存在的历史网卡"。

### A.3 已知库中同时存在"实体条目"与"幽灵条目"

```json
{ "guid": "{EC0DFDE6-17AB-4AB1-8756-ADCA3F917A10}", "name": "以太网 3",
  "description": "Remote NDIS based Internet Sharing Device #2", "interfaceType": "Ethernet",
  "priority": 1, "isIgnored": false, "firstSeenAt": "2026-09-12T00:23:26" }

{ "guid": "Remote NDIS based Internet Sharing Device",          ← 以设备名造键的幽灵条目
  "name": "Remote NDIS based Internet Sharing Device",
  "description": "", "interfaceType": "Other",
  "priority": 7, "isIgnored": true, "firstSeenAt": "2026-09-12T00:21:17" }
```

### A.4 仲裁日志：优先级 1 的网卡从未被选中，且状态反复

```
[2026-09-13 00:08:21] [INFO] 自动禁用网卡 以太网（优先级 2）      ← 此时 target 是优先级 1（以太网 3）
[2026-09-13 00:45:29] [INFO] 自动启用网卡 以太网（优先级 2）      ← 此时 target 变成优先级 2
[2026-09-13 00:45:37] [INFO] 自动禁用网卡 以太网（优先级 2）      ← 8 秒后（观察期结束）target 又回到优先级 1

# 全日志 2636 行中，出现 "以太网 3" 的行数：0
# 即：该网卡从未被启用或禁用过 —— 它从未进入 available
```

同时实测该设备在 40 秒连续采样窗口内保持稳定（`oper=1 / admin=1 / media=1`），说明它是**间歇性**掉出活跃通道，而非长期不可用——这正对应 §7.5 D1 的"窗口期误判"。

## 17. 附录 B：文件级实施清单（V1.1）

| # | 文件 | 改动 |
|---|---|---|
| 1 | `NetSwitch.Core/Models/AdapterPresence.cs` | 新增枚举 `Present` / `Absent` |
| 2 | `NetSwitch.Core/Models/AdapterLinkState.cs` | 新增枚举 `Up` / `Disconnected` / `Disabled` / `Unknown` |
| 3 | `NetSwitch.Core/Models/Adapter.cs` | 增加 `Presence`、`LinkState`、`PnpDeviceId`、`Raw`；保留 `State` 作为聚合视图 |
| 4 | `NetSwitch.Core/Models/AdapterState.cs` | 保留为展示层聚合枚举，注释说明其派生规则 |
| 5 | `NetSwitch.Core/Models/AppConfig.cs` | 新增 `StartSilently`（默认 `true`） |
| 6 | `NetSwitch.Core/Abstractions/IAdapterRepository.cs` | 可选新增 `IsDevicePresent(string pnpDeviceId)` |
| 7 | `NetSwitch.Infrastructure/Cim/CimAdapterRepository.cs` | ① 新增 `Win32_PnPEntity` 存在性集合（大小写不敏感）；② 重写 `MapActiveState()`（admin 优先、`oper==6` 不再直接判不存在）；③ 历史通道：按 `GUID`→`PNPDeviceID` 建键、两者皆缺则不登记、`Presence` 按 PnP 命中判定、快照内 GUID 去重（活跃优先） |
| 7a | `NetSwitch.Infrastructure/Cim/AdapterStateMapper.cs` | **新增**：WMI 原始值 → 领域状态映射规则（`MapActive` / `MapHistorical`），抽成纯函数以便 T4/T5 脱离真实硬件测试 |
| 7b | `NetSwitch.Infrastructure/Cim/PnpPresenceSet.cs` | **新增**：PnP 设备节点存在性集合，大小写不敏感匹配（T8） |
| 8 | `NetSwitch.Core/Arbitration/ArbitrationService.cs` | ① `available` 判据改用 `Presence`/`LinkState`；② 离开可用状态时清除 `_becameAvailableAt`；③ 状态变更时输出结构化诊断日志；④ 保留全部安全护栏 |
| 9 | `NetSwitch.App/ViewModels/AdapterItemViewModel.cs` | `Name`/`Description`/`InterfaceType` 改为可观察属性并在 `UpdateFrom()` 同步；新增 `Presence`、`IsInGracePeriod`；`StateText` 增加「状态未知」「· 观察中」 |
| 10 | `NetSwitch.App/ViewModels/MainViewModel.cs` | ① 快照容错建表（`TryAdd`）；② 新增 `StartSilently` 属性与持久化；③ 状态栏支持显示"观察中" |
| 11 | `NetSwitch.App/Services/StartupOptions.cs` | 新增：解析 `--silent` / `--show`，合并 `AppConfig.StartSilently` |
| 12 | `NetSwitch.App/Services/AutoStartService.cs` | ① 任务命令行追加 `--silent` 与 `/delay 0000:15`；② `IsEnabled()` 改为解析任务 XML 校验 exe 路径 / `--silent` / `HighestAvailable`；③ 新增 `EnsureUpToDate()` 自动修复 |
| 13 | `NetSwitch.App/App.xaml.cs` | ① 读取启动模式，静默时不 `Show()` 主窗口；② 首次仲裁前短延迟；③ 托盘创建失败时降级为显示窗口 |
| 14 | `NetSwitch.App/MainWindow.xaml` | 工具栏新增「静默启动」复选框 |
| 15 | `NetSwitch.App/Services/TrayIconService.cs` | 托盘菜单新增「开机自启」「静默启动」勾选项 |
| 16 | `NetSwitch.Tests/ArbitrationServiceTests.cs`、`Fakes.cs` | 新增 §12 的 T1~T8 用例（假仓库需支持 `Presence`/`LinkState`） |
| 17 | `README.md` | 同步"静默启动"与状态语义说明 |
| 18 | `NetSwitch.Tests/StartSilentlyTests.cs` | **V1.2.1 新增**：首次运行默认值语义（无字段 → `false`、物化落盘、用户显式值优先），7 个用例 |

**V1.2 落地情况**：上表 17 项**全部完成**（另追加 3 项：`AdapterStateMapper`、`PnpPresenceSet`、`StartupOptions` 三个新文件已计入 7a/7b/11）。构建 0 警告 0 错误；`NetSwitch.Tests` 25 个用例全绿（15 个既有 + T1/T2/T3/T4/T5/T5b/T6/T7/T8/S8）。

**V1.2.1 增量**：第 18 项（`StartSilentlyTests`）+ `AppConfig.StartSilently` 改为 `bool?` 并引入 `FirstRunStartSilently` / `ResolveStartSilently()`；单元测试总数增至 **32**，全绿。

---

## 18. 附录 C：V1.2 真机验证证据

环境：Windows，`D:\NetSwitch`，构建配置 Release，运行进程 `NetSwitch.App.exe`（PID 31696，由计划任务拉起）。日志：`%APPDATA%\NetSwitch\logs\netswitch-20260913.log`。

### C.1 D1 修复（核心验收项）

```
[01:37:39] [INFO] 网卡状态变更 guid={EC0DFDE6-17AB-4AB1-8756-ADCA3F917A10} name=以太网 3
                  presence=Present link=Up oper=1 admin=1 media=1
                  pnp=USB\VID_2D95&PID_600A&MI_00\8&324bd37&0&0000
```

优先级 1 的「以太网 3」（USB RNDIS 设备）被判定为 **存在 + 已连接**，界面显示「已连接」。对比 V1.0：同一设备曾显示「不存在」，且日志中「以太网 3」出现 0 次。

### C.2 计划任务自校验与自动修复

```
[01:33:35] [WARN] 计划任务 NetSwitchAutoStart 已失效，正在重建。
                  期望 exe=D:\NetSwitch\NetSwitch.App\bin\Release\net10.0-windows\NetSwitch.App.exe
                  参数含 --silent 运行级别=HighestAvailable；
                  实际 command="D:\NetSwitch\publish\NetSwitch.App.exe" args= runLevel=HighestAvailable
```

旧任务指向 `publish\` 目录且缺少 `--silent`，被正确识别为"需修复"并自动重建（对应 §10.2 与验证项 S1/S7）。

### C.3 静默启动与命令行优先级

```
[01:37:35] [INFO] 启动模式=Silent，配置 StartSilently=False，实际静默   ← 任务带 --silent，覆盖配置
[01:33:56] [INFO] 启动模式=Show，  配置 StartSilently=True，  实际显示窗口 ← --show 覆盖配置
[01:33:35] [INFO] 启动模式=Unspecified，配置 StartSilently=True，实际静默  ← 无命令行参数时取配置
```

三种组合全部符合 §10.3 的启动行为矩阵。同时验证 S4（单实例唤起）：带 `--show` 的第二个实例通过 `NetSwitch.ShowWindow` 命名事件唤起已有实例的主窗口，自身退出，未产生第二个常驻进程。

### C.4 幽灵条目存量清理（D5）

```
[01:34:00] [WARN] 已清理 2 条无法再生的遗留幽灵条目（键既非 GUID 也非 PnP 设备 ID）
```

清理后已知库剩 5 条，全部为带 GUID 的实体条目；原先以设备名 `Remote NDIS based Internet Sharing Device` 为键、描述为空的幽灵条目已消失。同时历史通道对缺失 `GUID` 与 `PNPDeviceID` 的设备（`Xbox Wireless Adapter for Windows`）只跳过登记、不再造键。

### C.5 诊断日志去重（V1.2 追加修复）

修复前：`历史网卡缺少 GUID 与 PNPDeviceID，已跳过登记` 与 `枚举 Win32_PnPEntity 失败` 会**每个轮询周期**重复输出（机主轮询间隔为 1 秒，实测每 tick 刷 2 条）。修复后按设备名去重，本次启动日志共 43 行，两条 WARN 各只出现 1 次。

### C.6 稳定性观测

在同一观测窗口内连续采样 65 秒：进程全程存活，日志无异常、无 `NetSwitch 退出` 记录，日志增量稳定不再增长。

### C.7 已知限制

- **`requireAdministrator` 导致无法静默拉起**：任何启动方式（含 `--silent`）都需经 UAC 提权一次，无法做到"零交互启动"。`/rl highest` 保证的是**登录时**不弹 UAC（任务以最高权限在交互会话中运行）。
- **`Environment.GetFolderPath` 在受限会话下可能返回空串**：已在 `JsonConfigStore` 中加 `USERPROFILE` 兜底，避免把配置写到当前工作目录。
