namespace NetSwitch.Core.Models;

/// <summary>
/// 网卡「设备是否存在」这一维度（与 <see cref="AdapterLinkState"/> 正交）。
///
/// V1.0 用单一 <see cref="AdapterState"/> 同时表达「设备在不在」和「链路通不通」，
/// 导致设备物理存在、但驱动未上报 / 链路未就绪时被误判为「不存在」（D1 缺陷）。
/// V1.1 起存在性以 PnP 设备节点（<c>Win32_PnPEntity</c>）为准，不再依赖
/// 「活跃通道此刻是否枚举到它」。
/// </summary>
public enum AdapterPresence
{
    /// <summary>设备节点存在于系统中（PnP 命中，或活跃通道命中）。</summary>
    Present,

    /// <summary>设备节点确实不在系统中（仅历史通道留有记录）。</summary>
    Absent,
}
