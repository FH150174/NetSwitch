namespace NetSwitch.Infrastructure.Cim;

/// <summary>
/// PnP 设备节点存在性集合，用于判定网卡「设备在不在」（<c>AdapterPresence</c>）。
///
/// <b>匹配必须大小写不敏感</b>：实测同一张网卡的 <c>PnpDeviceID</c> 在
/// <c>MSFT_NetAdapter</c> 中为小写（<c>...\8&amp;324bd37&amp;0&amp;0000</c>），
/// 在 <c>Win32_PnPEntity</c> / <c>Win32_NetworkAdapter</c> 中为大写
/// （<c>...\8&amp;324BD37&amp;0&amp;0000</c>）。若按序数比较，物理存在的设备会被误判为不存在。
/// </summary>
public sealed class PnpPresenceSet
{
    private readonly HashSet<string> _deviceIds;

    public PnpPresenceSet(IEnumerable<string?> deviceIds)
    {
        _deviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in deviceIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                _deviceIds.Add(id.Trim());
            }
        }
    }

    /// <summary>集合中已登记的设备节点数量。</summary>
    public int Count => _deviceIds.Count;

    /// <summary>空集合（WMI 查询失败时的降级值）。</summary>
    public static PnpPresenceSet Empty { get; } = new(Array.Empty<string?>());

    /// <summary>判定设备节点是否在集合中；<paramref name="pnpDeviceId"/> 为空时返回 <c>false</c>。</summary>
    public bool Contains(string? pnpDeviceId)
        => !string.IsNullOrWhiteSpace(pnpDeviceId) && _deviceIds.Contains(pnpDeviceId.Trim());
}
