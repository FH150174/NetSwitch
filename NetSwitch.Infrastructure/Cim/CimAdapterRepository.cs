using System.Management;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Infrastructure.Cim;

/// <summary>
/// 网卡仓库：以 WMI 的 <c>MSFT_NetAdapter</c>（root/StandardCimv2）枚举当前活跃网卡，
/// 并以 <c>Win32_NetworkAdapter</c>（root/CIMv2）补充「曾经接入过、现已不存在」的历史网卡，
/// 使拔掉/移除的设备仍能显示为 NotPresent。需要管理员权限。
/// 使用 <see cref="System.Management"/>（纯托管 COM interop），
/// 避免 Microsoft.Management.Infrastructure 2.0.0 在 .NET 8+ RID 图下的运行时缺失问题。
/// </summary>
public sealed class CimAdapterRepository : IAdapterRepository
{
    private const string StandardNamespace = "root\\StandardCimv2";
    private const string NetAdapterClass = "MSFT_NetAdapter";
    private const string Cimv2Namespace = "root\\CIMv2";
    private const string Win32AdapterClass = "Win32_NetworkAdapter";

    public IReadOnlyList<Adapter> GetPresentAdapters()
    {
        var active = EnumerateActiveAdapters();
        var activeGuids = active
            .Select(a => a.Guid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var historical = EnumerateHistoricalAdapters(activeGuids);
        return active.Concat(historical).ToList();
    }

    public void Enable(Adapter adapter) => InvokeMethod(adapter.Guid, "Enable");

    public void Disable(Adapter adapter) => InvokeMethod(adapter.Guid, "Disable");

    private static IReadOnlyList<Adapter> EnumerateActiveAdapters()
    {
        var result = new List<Adapter>();
        using var searcher = new ManagementObjectSearcher(StandardNamespace, $"SELECT * FROM {NetAdapterClass}");
        using var collection = searcher.Get();
        foreach (ManagementObject instance in collection)
        {
            using (instance)
            {
                var guid = GetString(instance, "InterfaceGuid")
                    ?? GetString(instance, "InstanceID")
                    ?? string.Empty;
                if (string.IsNullOrEmpty(guid))
                {
                    continue;
                }

                var name = GetString(instance, "Name")
                    ?? GetString(instance, "InterfaceAlias")
                    ?? string.Empty;
                var description = GetString(instance, "InterfaceDescription") ?? string.Empty;
                result.Add(new Adapter(guid, name, description,
                    MapActiveInterfaceType(instance), MapActiveState(instance)));
            }
        }

        return result;
    }

    private static IReadOnlyList<Adapter> EnumerateHistoricalAdapters(ISet<string> activeGuids)
    {
        var result = new List<Adapter>();
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
                if (!string.IsNullOrEmpty(guid) && activeGuids.Contains(guid))
                {
                    continue; // 活跃网卡已在 MSFT_NetAdapter 中枚举。
                }

                // 历史网卡可能没有 GUID，用 PNPDeviceID 兜底，最后退化为设备名作为稳定标识。
                var key = !string.IsNullOrEmpty(guid)
                    ? guid
                    : GetString(instance, "PNPDeviceID") ?? GetString(instance, "Name") ?? string.Empty;
                if (string.IsNullOrEmpty(key) || !seen.Add(key))
                {
                    continue; // 无稳定标识或重复，跳过。
                }

                var connName = GetString(instance, "NetConnectionID");
                var devName = GetString(instance, "Name") ?? string.Empty;
                var name = string.IsNullOrEmpty(connName) ? devName : connName;
                var description = string.IsNullOrEmpty(connName) ? string.Empty : devName;

                result.Add(new Adapter(key, name, description,
                    MapHistoricalInterfaceType(instance), AdapterState.NotPresent));
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

    private static AdapterState MapActiveState(ManagementObject instance)
    {
        // 依据微软 NetAdapter.Types.ps1xml 中 Status 的真实推导逻辑：
        // InterfaceOperationalStatus：1=Up，2=Down，6=NotPresent。
        // InterfaceAdminStatus：2=Down（被禁用）。
        var oper = GetUInt32(instance, "InterfaceOperationalStatus");
        var admin = GetUInt32(instance, "InterfaceAdminStatus");
        return oper switch
        {
            1 => AdapterState.Up,
            2 => admin == 2 ? AdapterState.Disabled : AdapterState.Disconnected,
            6 => AdapterState.NotPresent,
            _ => AdapterState.Disconnected,
        };
    }

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
}
