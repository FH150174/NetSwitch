namespace NetSwitch.Core.Models;

/// <summary>
/// 当前系统快照中的一个网卡，用于仲裁决策。
///
/// V1.1 起把 V1.0 的单一 <see cref="AdapterState"/> 拆成两个正交维度
/// <see cref="Presence"/>（设备在不在）与 <see cref="LinkState"/>（链路通不通），
/// <see cref="State"/> 仅作为二者的派生聚合视图供 UI 使用。
/// </summary>
/// <param name="Guid">网卡全局唯一标识（CIM 的 <c>InterfaceGuid</c> / <c>NetCfgInstanceId</c>）。</param>
/// <param name="Name">接口别名，如「以太网 3」。</param>
/// <param name="InterfaceDescription">接口描述，如「Remote NDIS based Internet Sharing Device #2」。</param>
/// <param name="InterfaceType">接口类型。</param>
/// <param name="Presence">设备是否存在。</param>
/// <param name="LinkState">链路可用性。</param>
/// <param name="PnpDeviceId">PnP 设备节点 ID，用于存在性核对；可能为 <c>null</c>。</param>
/// <param name="Raw">原始诊断串（oper/admin/media），仅用于日志，不参与逻辑判断。</param>
public sealed record Adapter(
    string Guid,
    string Name,
    string InterfaceDescription,
    InterfaceType InterfaceType,
    AdapterPresence Presence,
    AdapterLinkState LinkState,
    string? PnpDeviceId = null,
    string? Raw = null)
{
    /// <summary>
    /// 展示层聚合视图：<c>Presence == Absent</c> → <see cref="AdapterState.NotPresent"/>；
    /// 否则取 <see cref="LinkState"/>。
    /// </summary>
    public AdapterState State => Presence == AdapterPresence.Absent
        ? AdapterState.NotPresent
        : LinkState switch
        {
            AdapterLinkState.Up => AdapterState.Up,
            AdapterLinkState.Disconnected => AdapterState.Disconnected,
            AdapterLinkState.Disabled => AdapterState.Disabled,
            _ => AdapterState.Unknown,
        };

    /// <summary>设备存在时的构造入口。</summary>
    public static Adapter Present(
        string guid,
        string name,
        string interfaceDescription,
        InterfaceType interfaceType,
        AdapterLinkState linkState,
        string? pnpDeviceId = null,
        string? raw = null)
        => new(guid, name, interfaceDescription, interfaceType,
            AdapterPresence.Present, linkState, pnpDeviceId, raw);

    /// <summary>设备确实不存在时的构造入口。</summary>
    public static Adapter Absent(
        string guid,
        string name,
        string interfaceDescription,
        InterfaceType interfaceType,
        string? pnpDeviceId = null)
        => new(guid, name, interfaceDescription, interfaceType,
            AdapterPresence.Absent, AdapterLinkState.Unknown, pnpDeviceId);
}
