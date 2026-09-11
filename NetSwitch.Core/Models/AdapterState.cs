namespace NetSwitch.Core.Models;

/// <summary>
/// 网卡当前状态。
/// </summary>
public enum AdapterState
{
    /// <summary>已启用且网络连接已建立。</summary>
    Up,

    /// <summary>已启用但网络断开（网线未插 / 未连接 WiFi）。</summary>
    Disconnected,

    /// <summary>已被禁用。</summary>
    Disabled,

    /// <summary>设备不存在于系统。</summary>
    NotPresent,
}
