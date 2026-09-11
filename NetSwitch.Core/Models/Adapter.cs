namespace NetSwitch.Core.Models;

/// <summary>
/// 当前系统快照中的一个网卡，用于仲裁决策。
/// </summary>
/// <param name="Guid">网卡全局唯一标识（CIM 的 DeviceID）。</param>
/// <param name="Name">接口名，如「以太网」。</param>
/// <param name="InterfaceDescription">接口描述，如「Realtek PCIe GbE Family Controller」。</param>
/// <param name="InterfaceType">接口类型。</param>
/// <param name="State">当前状态。</param>
public sealed record Adapter(
    string Guid,
    string Name,
    string InterfaceDescription,
    InterfaceType InterfaceType,
    AdapterState State);
