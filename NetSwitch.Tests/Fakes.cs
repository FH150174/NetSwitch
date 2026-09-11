using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Tests;

/// <summary>
/// 假网卡仓库：内存中维护网卡快照，记录 Enable/Disable 调用。
/// </summary>
public sealed class FakeAdapterRepository : IAdapterRepository
{
    public Dictionary<string, Adapter> Adapters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>为 true 时 Enable/Disable 立即反映到状态，模拟操作即时生效。</summary>
    public bool ApplyImmediately { get; set; } = true;

    public List<string> EnabledLog { get; } = new();
    public List<string> DisabledLog { get; } = new();

    public void Add(Adapter adapter) => Adapters[adapter.Guid] = adapter;

    public IReadOnlyList<Adapter> GetPresentAdapters() => Adapters.Values.ToList();

    public void Enable(Adapter adapter)
    {
        EnabledLog.Add(adapter.Guid);
        if (ApplyImmediately)
        {
            SetState(adapter.Guid, AdapterState.Up);
        }
    }

    public void Disable(Adapter adapter)
    {
        DisabledLog.Add(adapter.Guid);
        if (ApplyImmediately)
        {
            SetState(adapter.Guid, AdapterState.Disabled);
        }
    }

    public void SetState(string guid, AdapterState state)
    {
        Adapters[guid] = Adapters[guid] with { State = state };
    }
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

    public void Notify(string title, string message) => Messages.Add((title, message));
}

internal static class AdapterFactory
{
    public static Adapter Adapter(
        string guid,
        string name,
        AdapterState state = AdapterState.Up,
        InterfaceType type = InterfaceType.Ethernet)
        => new(guid, name, $"{name} Description", type, state);

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
