using NetSwitch.Core.Models;

namespace NetSwitch.Core.Abstractions;

/// <summary>
/// 网卡数据访问抽象。仲裁逻辑只依赖此接口，测试用假实现注入。
/// </summary>
public interface IAdapterRepository
{
    /// <summary>获取当前系统里存在的所有网卡快照。</summary>
    IReadOnlyList<Adapter> GetPresentAdapters();

    void Enable(Adapter adapter);

    void Disable(Adapter adapter);
}
