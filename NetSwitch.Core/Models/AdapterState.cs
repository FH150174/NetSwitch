namespace NetSwitch.Core.Models;

/// <summary>
/// 网卡状态的**展示层聚合视图**（V1.1 起降级，不再作为领域主模型）。
///
/// 派生规则（见 <see cref="Adapter.State"/>）：
/// <list type="bullet">
/// <item><c>Presence == Absent</c> → <see cref="NotPresent"/>。</item>
/// <item>否则取 <see cref="AdapterLinkState"/> 的对应值；<c>Unknown</c> → <see cref="Unknown"/>。</item>
/// </list>
///
/// <b>仲裁逻辑不得依赖本枚举</b>，必须直接读 <see cref="Adapter.Presence"/> 与
/// <see cref="Adapter.LinkState"/>——把两个正交维度压成一个值正是 D1 缺陷的根源。
/// 本枚举仅供 UI 绑定与兼容保留。
/// </summary>
public enum AdapterState
{
    /// <summary>已启用且网络连接已建立。</summary>
    Up,

    /// <summary>已启用但网络断开（网线未插 / 未连接 WiFi）。</summary>
    Disconnected,

    /// <summary>已被禁用。</summary>
    Disabled,

    /// <summary>状态无法判定（设备存在，但驱动未上报可判定的链路状态）。</summary>
    Unknown,

    /// <summary>设备确实不存在于系统（仅此时使用）。</summary>
    NotPresent,
}
