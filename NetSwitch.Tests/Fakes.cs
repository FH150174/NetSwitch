using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

// AdapterFactory 内的方法名 Adapter 会遮蔽类型名 Adapter，用别名规避。
using AdapterModel = NetSwitch.Core.Models.Adapter;

namespace NetSwitch.Tests;

/// <summary>
/// 假网卡仓库：内存中维护网卡快照，记录 Enable/Disable 调用。
/// </summary>
public sealed class FakeAdapterRepository : IAdapterRepository
{
    public Dictionary<string, Adapter> Adapters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 额外原样塞进快照的条目（不经过字典），用于构造「重复 GUID」等异常快照（T6）。
    /// </summary>
    public List<Adapter> RawExtras { get; } = new();

    /// <summary>为 true 时 Enable/Disable 立即反映到状态，模拟操作即时生效。</summary>
    public bool ApplyImmediately { get; set; } = true;

    public List<string> EnabledLog { get; } = new();
    public List<string> DisabledLog { get; } = new();

    public void Add(Adapter adapter) => Adapters[adapter.Guid] = adapter;

    public IReadOnlyList<Adapter> GetPresentAdapters()
        => Adapters.Values.Concat(RawExtras).ToList();

    public void Enable(Adapter adapter)
    {
        EnabledLog.Add(adapter.Guid);
        if (ApplyImmediately)
        {
            SetLinkState(adapter.Guid, AdapterLinkState.Up);
        }
    }

    public void Disable(Adapter adapter)
    {
        DisabledLog.Add(adapter.Guid);
        if (ApplyImmediately)
        {
            SetLinkState(adapter.Guid, AdapterLinkState.Disabled);
        }
    }

    /// <summary>设置链路状态；同时把设备标记为存在（能操作说明设备在）。</summary>
    public void SetLinkState(string guid, AdapterLinkState linkState)
        => Adapters[guid] = Adapters[guid] with
        {
            LinkState = linkState,
            Presence = AdapterPresence.Present,
        };

    /// <summary>设置存在性（模拟设备被拔出 / 插回）。</summary>
    public void SetPresence(string guid, AdapterPresence presence)
        => Adapters[guid] = Adapters[guid] with { Presence = presence };

    public bool IsDevicePresent(string? pnpDeviceId)
        => !string.IsNullOrWhiteSpace(pnpDeviceId)
           && Adapters.Values.Any(a =>
               string.Equals(a.PnpDeviceId, pnpDeviceId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 内存配置存储。
/// </summary>
public sealed class InMemoryConfigStore : IConfigStore
{
    public AppConfig Config { get; set; } = new();

    public AppConfig Load() => Config;

    public void Save(AppConfig config) => Config = config;
}

/// <summary>
/// 记录通知消息的假通知器。
/// </summary>
public sealed class FakeNotifier : INotifier
{
    public List<(string Title, string Message)> Messages { get; } = new();

    /// <summary>为 true 时抛异常，用于验证「通知失败绝不中断仲裁」（S8）。</summary>
    public bool ThrowOnNotify { get; set; }

    public void Notify(string title, string message)
    {
        if (ThrowOnNotify)
        {
            throw new InvalidOperationException("模拟托盘通知失败（TrayIcon is not created）");
        }

        Messages.Add((title, message));
    }
}

internal static class AdapterFactory
{
    /// <summary>构造一个「设备存在」的网卡。</summary>
    public static Adapter Adapter(
        string guid,
        string name,
        AdapterLinkState linkState = AdapterLinkState.Up,
        InterfaceType type = InterfaceType.Ethernet,
        string? pnpDeviceId = null)
        => AdapterModel.Present(guid, name, $"{name} Description", type, linkState, pnpDeviceId);

    /// <summary>构造一个「设备不存在」的网卡（仅历史通道有记录）。</summary>
    public static Adapter Absent(
        string guid,
        string name,
        InterfaceType type = InterfaceType.Ethernet,
        string? pnpDeviceId = null)
        => AdapterModel.Absent(guid, name, $"{name} Description", type, pnpDeviceId);

    public static KnownAdapter Known(
        string guid,
        int priority,
        bool isIgnored = false)
        => new()
        {
            Guid = guid,
            Name = guid,
            Priority = priority,
            IsIgnored = isIgnored,
            FirstSeenAt = DateTime.Now,
            LastSeenAt = DateTime.Now,
        };
}
