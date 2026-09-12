using NetSwitch.Core.Models;

namespace NetSwitch.Infrastructure.Cim;

/// <summary>
/// WMI 原始状态到领域模型的映射规则（规格 §7.5 D1b）。
/// 抽成纯函数以便脱离真实硬件做单元测试。
/// </summary>
public static class AdapterStateMapper
{
    /// <summary>
    /// 活跃通道（<c>MSFT_NetAdapter</c>）的状态映射。判定顺序不可调换。
    /// </summary>
    /// <param name="operationalStatus">
    /// <c>InterfaceOperationalStatus</c>：1=Up，2=Down，5=Dormant，6=NotPresent，7=LowerLayerDown。
    /// </param>
    /// <param name="adminStatus"><c>InterfaceAdminStatus</c>：2=Down（被管理员禁用）。</param>
    /// <param name="mediaConnectState"><c>MediaConnectState</c>：1=Connected。</param>
    /// <param name="pnpDevicePresent">PnP 设备节点是否存在。</param>
    public static (AdapterPresence Presence, AdapterLinkState LinkState) MapActive(
        uint? operationalStatus,
        uint? adminStatus,
        uint? mediaConnectState,
        bool pnpDevicePresent)
    {
        // 1) 管理员禁用：最高优先，先于一切判断，且绝不等同于「设备不存在」。
        if (adminStatus == 2)
        {
            return (AdapterPresence.Present, AdapterLinkState.Disabled);
        }

        switch (operationalStatus)
        {
            // 2) 已启用且媒体已连接；媒体状态缺失时不视为矛盾，按 oper 判定为 Up。
            case 1 when mediaConnectState is null or 1:
                return (AdapterPresence.Present, AdapterLinkState.Up);

            // 3) 已启用但链路未就绪：媒体未连接 / Down / Dormant / LowerLayerDown。
            case 1:
            case 2:
            case 5:
            case 7:
                return (AdapterPresence.Present, AdapterLinkState.Disconnected);

            // 4/5) NotPresent：以 PnP 设备节点为准，绝不无条件判「不存在」。
            // RNDIS/USB 类驱动在「已禁用」或「链路未就绪」时会上报 oper == 6，
            // 若无条件判「不存在」就会重现 D1 缺陷（优先级 1 网卡显示为不存在）。
            case 6:
                return pnpDevicePresent
                    ? (AdapterPresence.Present, AdapterLinkState.Disconnected)
                    : (AdapterPresence.Absent, AdapterLinkState.Unknown);

            // 6) 属性缺失 / 无法判定：保留设备存在性，链路状态标为未知。
            default:
                return (AdapterPresence.Present, AdapterLinkState.Unknown);
        }
    }

    /// <summary>
    /// 历史通道（<c>Win32_NetworkAdapter</c>）的链路状态推断。
    /// 仅用于「设备节点存在但活跃通道未枚举到」的窗口期；推不出时返回
    /// <see cref="AdapterLinkState.Unknown"/>，绝不猜成 Disabled 或不存在。
    /// </summary>
    /// <param name="netConnectionStatus">
    /// 0=Disconnected，1=Connecting，2=Connected，3=Disconnecting，4=HardwareNotPresent，
    /// 5=HardwareDisabled，6=HardwareMalfunction，7=MediaDisconnected，…
    /// </param>
    /// <param name="netEnabled">适配器是否已启用；<c>null</c> 表示驱动未上报。</param>
    public static AdapterLinkState MapHistorical(uint? netConnectionStatus, bool? netEnabled)
    {
        if (netConnectionStatus == 5 || netEnabled is false)
        {
            return AdapterLinkState.Disabled;
        }

        return netConnectionStatus switch
        {
            2 => AdapterLinkState.Up,
            0 or 1 or 3 or 7 => AdapterLinkState.Disconnected,
            _ => AdapterLinkState.Unknown,
        };
    }
}
