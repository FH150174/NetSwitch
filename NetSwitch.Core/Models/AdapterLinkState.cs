namespace NetSwitch.Core.Models;

/// <summary>
/// 网卡「链路可用性」这一维度，仅在 <see cref="AdapterPresence.Present"/> 时有意义。
///
/// 与 <see cref="AdapterPresence"/> 正交：设备可以存在但链路未就绪（网线未插），
/// 也可以被管理员禁用；这些都**不等于**「设备不存在」。
/// </summary>
public enum AdapterLinkState
{
    /// <summary>已启用且链路已连接（可联网）。</summary>
    Up,

    /// <summary>已启用但链路未就绪（网线未插 / 未连 WiFi / 媒体断开）。</summary>
    Disconnected,

    /// <summary>被管理员禁用（<c>InterfaceAdminStatus == 2</c>）。</summary>
    Disabled,

    /// <summary>无法判定（属性缺失、驱动未上报，或仅历史通道可见）。</summary>
    Unknown,
}
