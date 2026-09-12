using NetSwitch.Core.Models;

namespace NetSwitch.Core.Abstractions;

/// <summary>
/// 网卡数据访问抽象。仲裁逻辑只依赖此接口，测试用假实现注入。
/// </summary>
public interface IAdapterRepository
{
    /// <summary>
    /// 获取当前系统里存在的所有网卡快照。
    /// 实现必须保证：快照内 <see cref="Adapter.Guid"/> 唯一（活跃通道优先，见规格 §7.5 D4）。
    /// </summary>
    IReadOnlyList<Adapter> GetPresentAdapters();

    /// <summary>启用网卡（需要管理员权限）。</summary>
    void Enable(Adapter adapter);

    /// <summary>禁用网卡（需要管理员权限）。</summary>
    void Disable(Adapter adapter);

    /// <summary>
    /// 判定某个 PnP 设备节点当前是否存在于系统中。
    /// 匹配必须**大小写不敏感**——实测同一设备的 <c>PnpDeviceID</c> 在
    /// <c>MSFT_NetAdapter</c> 中为小写、在 <c>Win32_PnPEntity</c> 中为大写。
    /// </summary>
    /// <param name="pnpDeviceId">PnP 设备 ID；为空时返回 <c>false</c>。</param>
    bool IsDevicePresent(string? pnpDeviceId);
}
