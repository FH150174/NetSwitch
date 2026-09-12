using System.Management;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Infrastructure.Cim;

/// <summary>
/// 网卡仓库。枚举三条 WMI 通道并合成快照：
/// <list type="number">
/// <item><c>Win32_PnPEntity WHERE PNPClass = 'Net'</c>（root/CIMV2）——<b>存在性权威来源</b>（V1.1 新增）。</item>
/// <item><c>MSFT_NetAdapter</c>（root/StandardCimv2）——当前活跃网卡及其真实链路状态。</item>
/// <item><c>Win32_NetworkAdapter</c>（root/CIMV2）——补充「曾经接入过、现已移除」的历史网卡。</item>
/// </list>
///
/// V1.1 关键修正（规格 §7.5）：
/// <list type="bullet">
/// <item>存在性以 PnP 设备节点为准，<b>不再</b>由「活跃通道此刻是否枚举到」决定（D1）。</item>
/// <item><c>InterfaceOperationalStatus == 6</c> 不再无条件判为「不存在」（D1b）。</item>
/// <item>历史通道建键固定为 <c>GUID</c> → <c>PNPDeviceID</c>，两者皆缺则不登记（D5）。</item>
/// <item>快照内 GUID 唯一，活跃通道优先（D4）。</item>
/// </list>
/// 需要管理员权限。使用 <see cref="System.Management"/>（纯托管 COM interop），
/// 避免 Microsoft.Management.Infrastructure 2.0.0 在 .NET 8+ RID 图下的运行时缺失问题。
/// </summary>
public sealed class CimAdapterRepository : IAdapterRepository
{
    private const string StandardNamespace = "root\\StandardCimv2";
    private const string NetAdapterClass = "MSFT_NetAdapter";
    private const string Cimv2Namespace = "root\\CIMV2";
    private const string Win32AdapterClass = "Win32_NetworkAdapter";
    private const string PnpEntityClass = "Win32_PnPEntity";

    /// <summary>仲裁与 UI 只读日志，不依赖注入的日志实现。</summary>
    public Action<string>? Warn { get; init; }

    /// <summary>已告警过「缺少稳定标识」的设备名，避免每个轮询周期重复刷屏。</summary>
    private readonly HashSet<string> _warnedSkippedDevices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否已告警过 PnP 枚举失败（同样只在首次告警，避免刷屏）。</summary>
    private bool _warnedPnpFailure;

    public IReadOnlyList<Adapter> GetPresentAdapters()
    {
        // 1) 先建存在性集合：后续所有「设备在不在」的判断都以此为准。
        var pnpPresent = EnumeratePnpNetworkDevices();

        // 2) 活跃通道：状态最准确，且一律视为 Present。
        var active = EnumerateActiveAdapters(pnpPresent);

        // 3) 历史通道：补「已移除」的设备；与活跃通道 GUID 冲突时活跃优先。
        var historical = EnumerateHistoricalAdapters(active, pnpPresent);

        return active.Concat(historical).ToList();
    }

    public void Enable(Adapter adapter) => InvokeMethod(adapter.Guid, "Enable");

    public void Disable(Adapter adapter) => InvokeMethod(adapter.Guid, "Disable");

    public bool IsDevicePresent(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId))
        {
            return false;
        }

        return EnumeratePnpNetworkDevices().Contains(pnpDeviceId);
    }

    /// <summary>
    /// 枚举 PnP 网络设备节点，作为「设备是否存在」的权威依据。
    /// 查询失败时降级为空集合（宁可把设备判为不存在而保持不动作，也不误判为存在而错误仲裁）。
    /// </summary>
    private PnpPresenceSet EnumeratePnpNetworkDevices()
    {
        try
        {
            var ids = new List<string?>();
            using var searcher = new ManagementObjectSearcher(
                Cimv2Namespace, $"SELECT DeviceID FROM {PnpEntityClass} WHERE PNPClass = 'Net'");
            using var collection = searcher.Get();
            foreach (ManagementObject instance in collection)
            {
                using (instance)
                {
                    ids.Add(GetString(instance, "DeviceID"));
                }
            }

            return new PnpPresenceSet(ids);
        }
        catch (ManagementException ex)
        {
            if (!_warnedPnpFailure)
            {
                _warnedPnpFailure = true;
                Warn?.Invoke($"枚举 {PnpEntityClass} 失败，存在性判定降级: {ex.Message}");
            }

            return PnpPresenceSet.Empty;
        }
    }

    private static IReadOnlyList<Adapter> EnumerateActiveAdapters(PnpPresenceSet pnpPresent)
    {
        var result = new List<Adapter>();
        // 快照内 GUID 必须唯一：同一 GUID 重复出现时保留首条，避免 UI 建表抛异常（D4）。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var searcher = new ManagementObjectSearcher(StandardNamespace, $"SELECT * FROM {NetAdapterClass}");
        using var collection = searcher.Get();
        foreach (ManagementObject instance in collection)
        {
            using (instance)
            {
                var guid = GetString(instance, "InterfaceGuid")
                    ?? GetString(instance, "InstanceID")
                    ?? string.Empty;
                if (string.IsNullOrEmpty(guid) || !seen.Add(guid))
                {
                    continue;
                }

                var name = GetString(instance, "Name")
                    ?? GetString(instance, "InterfaceAlias")
                    ?? string.Empty;
                var description = GetString(instance, "InterfaceDescription") ?? string.Empty;
                var pnpDeviceId = GetString(instance, "PnpDeviceID");
                var (presence, linkState) = MapActiveState(instance, pnpPresent, pnpDeviceId);

                result.Add(new Adapter(
                    guid, name, description,
                    MapActiveInterfaceType(instance),
                    presence,
                    linkState,
                    pnpDeviceId,
                    BuildRawDiagnostics(instance)));
            }
        }

        return result;
    }

    private IReadOnlyList<Adapter> EnumerateHistoricalAdapters(
        IReadOnlyList<Adapter> active, PnpPresenceSet pnpPresent)
    {
        var result = new List<Adapter>();
        var activeGuids = active
            .Select(a => a.Guid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var searcher = new ManagementObjectSearcher(Cimv2Namespace, $"SELECT * FROM {Win32AdapterClass}");
        using var collection = searcher.Get();
        foreach (ManagementObject instance in collection)
        {
            using (instance)
            {
                // 跳过系统内置的虚拟网卡（WAN 微型端口、内核调试等），它们不是「曾经接入过」的设备。
                if (IsSystemVirtualAdapter(instance))
                {
                    continue;
                }

                var guid = GetString(instance, "GUID");
                var pnpDeviceId = GetString(instance, "PNPDeviceID");

                // 建键优先级固定为 GUID → PNPDeviceID。两者皆缺时不登记，
                // 避免退化成「用设备名造键」而产生无法与实体条目合并的幽灵条目（D5）。
                var key = !string.IsNullOrEmpty(guid) ? guid : pnpDeviceId;
                if (string.IsNullOrEmpty(key))
                {
                    var devName = GetString(instance, "Name") ?? "(未知设备)";

                    // 只在首次遇到时告警：轮询间隔默认 1~2 秒，每次刷屏会迅速淹没日志。
                    if (_warnedSkippedDevices.Add(devName))
                    {
                        Warn?.Invoke($"历史网卡缺少 GUID 与 PNPDeviceID，已跳过登记: {devName}");
                    }

                    continue;
                }

                if (activeGuids.Contains(key) || !seen.Add(key))
                {
                    continue; // 活跃通道已有该设备（活跃优先），或本轮已登记过。
                }

                var connName = GetString(instance, "NetConnectionID");
                var name = string.IsNullOrEmpty(connName) ? GetString(instance, "Name") ?? string.Empty : connName;
                var description = string.IsNullOrEmpty(connName) ? string.Empty : GetString(instance, "Name") ?? string.Empty;

                // 存在性以 PnP 设备节点为准：节点在 → Present（链路状态另行推断）；
                // 节点确实不在 → Absent。绝不因为「活跃通道没枚举到」就判为不存在。
                var isPresent = pnpPresent.Contains(pnpDeviceId);
                var adapter = isPresent
                    ? Adapter.Present(key, name, description, MapHistoricalInterfaceType(instance),
                        MapHistoricalLinkState(instance), pnpDeviceId, BuildRawDiagnostics(instance))
                    : Adapter.Absent(key, name, description, MapHistoricalInterfaceType(instance), pnpDeviceId);

                result.Add(adapter);
            }
        }

        return result;
    }

    private static void InvokeMethod(string guid, string methodName)
    {
        using var searcher = new ManagementObjectSearcher(
            StandardNamespace,
            $"SELECT * FROM {NetAdapterClass} WHERE InterfaceGuid = \"{guid}\"");
        using var collection = searcher.Get();
        using var instance = collection.Cast<ManagementObject>().FirstOrDefault();
        if (instance is null)
        {
            throw new InvalidOperationException($"未找到网卡: {guid}");
        }

        // 用 GetMethodParameters + InvokeMethod(inParams, options) 重载，
        // 规避无输出参数方法在 InvokeMethod(name, object[]) 的 MapOutParameters 中抛 NullReferenceException。
        var inParams = instance.GetMethodParameters(methodName);
        var outParams = instance.InvokeMethod(methodName, inParams, null);
        var code = outParams is null ? 0u : Convert.ToUInt32(outParams["ReturnValue"]);
        if (code != 0)
        {
            throw new InvalidOperationException($"网卡 {methodName} 失败，返回码 {code}");
        }
    }

    /// <summary>
    /// 活跃通道的状态映射（规格 §7.5 D1b）。判定顺序不可调换：
    /// 管理员禁用优先级最高，且 <c>oper == 6</c> 必须结合 PnP 设备节点判定，不得直接判「不存在」。
    /// 实际规则见 <see cref="AdapterStateMapper.MapActive"/>（纯函数，可单元测试）。
    /// </summary>
    private static (AdapterPresence Presence, AdapterLinkState LinkState) MapActiveState(
        ManagementObject instance, PnpPresenceSet pnpPresent, string? pnpDeviceId)
        => AdapterStateMapper.MapActive(
            GetUInt32(instance, "InterfaceOperationalStatus"),
            GetUInt32(instance, "InterfaceAdminStatus"),
            GetUInt32(instance, "MediaConnectState"),
            pnpPresent.Contains(pnpDeviceId));

    /// <summary>
    /// 历史通道的链路状态推断，规则见 <see cref="AdapterStateMapper.MapHistorical"/>。
    /// </summary>
    private static AdapterLinkState MapHistoricalLinkState(ManagementObject instance)
        => AdapterStateMapper.MapHistorical(
            GetUInt32(instance, "NetConnectionStatus"),
            GetNullableBool(instance, "NetEnabled"));

    private static InterfaceType MapActiveInterfaceType(ManagementObject instance)
    {
        if (GetBool(instance, "Virtual"))
        {
            return InterfaceType.Virtual;
        }

        // MIB ifType：71 = ieee80211（WiFi），6 = ethernetCsmacd（以太网）。
        return GetUInt32(instance, "InterfaceType") switch
        {
            71 => InterfaceType.WiFi,
            6 => InterfaceType.Ethernet,
            _ => InterfaceType.Other,
        };
    }

    private static InterfaceType MapHistoricalInterfaceType(ManagementObject instance)
    {
        var adapterType = GetString(instance, "AdapterType") ?? string.Empty;
        if (adapterType.Contains("802.11", StringComparison.OrdinalIgnoreCase)
            || adapterType.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
        {
            return InterfaceType.WiFi;
        }

        if (adapterType.Contains("802.3", StringComparison.OrdinalIgnoreCase)
            || adapterType.Contains("Ethernet", StringComparison.OrdinalIgnoreCase))
        {
            return InterfaceType.Ethernet;
        }

        return InterfaceType.Other;
    }

    private static bool IsSystemVirtualAdapter(ManagementObject instance)
    {
        var name = GetString(instance, "Name") ?? string.Empty;
        var pnp = GetString(instance, "PNPDeviceID") ?? string.Empty;
        return name.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)
            || pnp.StartsWith("SWD\\MSRRAS", StringComparison.OrdinalIgnoreCase)
            || pnp.StartsWith("ROOT\\KDNIC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>构造原始诊断串（oper/admin/media），供日志复现问题，不参与任何逻辑判断。</summary>
    private static string BuildRawDiagnostics(ManagementObject instance)
        => $"oper={GetUInt32(instance, "InterfaceOperationalStatus")?.ToString() ?? "-"}"
         + $" admin={GetUInt32(instance, "InterfaceAdminStatus")?.ToString() ?? "-"}"
         + $" media={GetUInt32(instance, "MediaConnectState")?.ToString() ?? "-"}";

    private static string? GetString(ManagementObject instance, string name)
        => instance[name]?.ToString();

    private static uint? GetUInt32(ManagementObject instance, string name)
    {
        var value = instance[name];
        return value is null ? null : Convert.ToUInt32(value);
    }

    private static bool GetBool(ManagementObject instance, string name)
    {
        var value = instance[name];
        return value is not null && Convert.ToBoolean(value);
    }

    private static bool? GetNullableBool(ManagementObject instance, string name)
    {
        var value = instance[name];
        return value is null ? null : Convert.ToBoolean(value);
    }
}
