using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.Core.Arbitration;

/// <summary>
/// 自动仲裁状态机。每个轮询周期执行一次 <see cref="Tick"/>，
/// 保证在已管理范围内只有一个「目标激活网卡」处于 Up 状态。
///
/// 安全护栏（任一命中即不执行任何操作）：
/// <list type="bullet">
/// <item>候选集为空（无已知网卡存在）。</item>
/// <item>可用集为空（所有候选均 Disconnected）。</item>
/// <item>自动模式关闭。</item>
/// </list>
/// </summary>
public sealed class ArbitrationService
{
    /// <summary>Enable/Disable 后的冷却时长，避免操作未生效造成的抖动。</summary>
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 「网卡由不可用变为可用」后的观察期。设备移除后 WMI 状态可能短暂反复
    /// （短暂出现又消失），观察期内不把刚出现的网卡当作仲裁目标，避免反复切换。
    /// </summary>
    private static readonly TimeSpan AvailabilityGracePeriod = TimeSpan.FromSeconds(8);

    private readonly IAdapterRepository _repository;
    private readonly IConfigStore _configStore;
    private readonly ILogger? _logger;
    private readonly INotifier? _notifier;
    private readonly Func<DateTime> _now;

    private readonly Dictionary<string, DateTime> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AdapterState> _lastStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _becameAvailableAt = new(StringComparer.OrdinalIgnoreCase);

    // Tick（后台线程）与 SwitchTo（UI 线程）共享冷却字典与仓库操作，需串行化。
    private readonly object _gate = new();

    public ArbitrationService(
        IAdapterRepository repository,
        IConfigStore configStore,
        ILogger? logger = null,
        INotifier? notifier = null,
        Func<DateTime>? nowProvider = null)
    {
        _repository = repository;
        _configStore = configStore;
        _logger = logger;
        _notifier = notifier;
        _now = nowProvider ?? (() => DateTime.Now);
    }

    /// <summary>
    /// 执行一个仲裁周期：刷新已知库、在自动模式下启停网卡、持久化。
    /// </summary>
    public void Tick()
    {
        lock (_gate)
        {
            TickCore();
        }
    }

    /// <summary>
    /// 手动一键切换到指定网卡。只对「已管理且未忽略」的网卡生效，
    /// 绝不触碰忽略列表与未知网卡；目标不可用时不做任何操作。
    /// 手动切换绕过冷却机制，立即执行，避免切换后数秒内无法再次切换。
    /// </summary>
    public void SwitchTo(string guid)
    {
        lock (_gate)
        {
            SwitchToCore(guid);
        }
    }

    private void TickCore()
    {
        var config = _configStore.Load();
        var present = _repository.GetPresentAdapters();
        var now = _now();

        if (config.AutoArbitrate)
        {
            Arbitrate(config, present, now);
        }

        UpdateKnownAdapters(config, present, now);
        _configStore.Save(config);
    }

    private void SwitchToCore(string guid)
    {
        var config = _configStore.Load();
        var known = config.KnownAdapters.FirstOrDefault(k =>
            string.Equals(k.Guid, guid, StringComparison.OrdinalIgnoreCase) && !k.IsIgnored);
        if (known is null)
        {
            _logger?.Warn($"手动切换目标不在已知库或已被忽略: {guid}");
            return;
        }

        var present = _repository.GetPresentAdapters();
        var adapter = present.FirstOrDefault(a =>
            string.Equals(a.Guid, guid, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            _logger?.Warn($"手动切换目标网卡不存在: {guid}");
            return;
        }

        var now = _now();

        // 目标必须先变为可用，否则不动作，避免切到断开的网线导致断网。
        // 手动切换是用户的即时意图：绕过冷却立即执行，避免切换后 3 秒内无法再次切换。
        var targetReady = adapter.State == AdapterState.Up;
        if (adapter.State == AdapterState.Disabled)
        {
            _repository.Enable(adapter);
            // 同步状态基线，避免后续自动仲裁误判为「刚出现」而撤销手动切换。
            _lastStates[adapter.Guid] = AdapterState.Up;
            EnterCooldown(adapter.Guid, now);
            targetReady = true;
            _logger?.Info($"手动启用网卡 {adapter.Name}");
            if (config.NotificationsEnabled)
            {
                Notify($"已切换到 {adapter.Name}");
            }
        }

        if (!targetReady)
        {
            _logger?.Warn($"目标网卡不可用（{adapter.State}），未执行切换: {adapter.Name}");
            return;
        }

        // 只禁用「已管理且未忽略」的其它在线网卡。
        var managed = config.KnownAdapters
            .Where(k => !k.IsIgnored)
            .ToDictionary(k => k.Guid, StringComparer.OrdinalIgnoreCase);

        foreach (var other in present.Where(a => a.Guid != adapter.Guid))
        {
            if (other.State != AdapterState.Up)
            {
                continue;
            }

            // 手动切换：同样绕过冷却，立即禁用其它在线网卡。
            if (!managed.ContainsKey(other.Guid))
            {
                continue;
            }

            _repository.Disable(other);
            _lastStates[other.Guid] = AdapterState.Disabled;
            EnterCooldown(other.Guid, now);
            _logger?.Info($"手动禁用网卡 {other.Name}");
            if (config.NotificationsEnabled)
            {
                Notify($"已禁用 {other.Name}");
            }
        }
    }

    /// <summary>
    /// 跟踪每个网卡的状态迁移，记录「由不可用变为可用」的时刻，
    /// 供仲裁在观察期内忽略刚出现的网卡，避免 WMI 状态抖动引起的反复切换。
    /// </summary>
    private void TrackStateTransitions(IReadOnlyList<Adapter> present, DateTime now)
    {
        foreach (var adapter in present)
        {
            if (_lastStates.TryGetValue(adapter.Guid, out var previous))
            {
                var wasAvailable = previous is AdapterState.Up or AdapterState.Disabled;
                var isAvailable = adapter.State is AdapterState.Up or AdapterState.Disabled;
                if (isAvailable && !wasAvailable)
                {
                    _becameAvailableAt[adapter.Guid] = now;
                }
            }

            _lastStates[adapter.Guid] = adapter.State;
        }
    }

    private void Arbitrate(AppConfig config, IReadOnlyList<Adapter> present, DateTime now)
    {
        var presentByGuid = present.ToDictionary(a => a.Guid, StringComparer.OrdinalIgnoreCase);

        TrackStateTransitions(present, now);

        // 候选 = 已管理（未忽略）且当前存在的网卡，按优先级升序。
        var candidates = config.KnownAdapters
            .Where(k => !k.IsIgnored && presentByGuid.ContainsKey(k.Guid))
            .OrderBy(k => k.Priority)
            .ToList();

        if (candidates.Count == 0)
        {
            return; // 安全：无任何已知网卡存在，不执行任何操作。
        }

        // 可用 = 有联网潜力（Up 或 Disabled）且状态稳定的候选。
        // 「刚由不可用变为可用」的网卡在观察期内不算可用，避免设备短暂出现/消失引起的震荡。
        var available = candidates
            .Where(k =>
            {
                if (presentByGuid[k.Guid].State is not (AdapterState.Up or AdapterState.Disabled))
                {
                    return false;
                }

                return !_becameAvailableAt.TryGetValue(k.Guid, out var t) || now - t >= AvailabilityGracePeriod;
            })
            .ToList();

        if (available.Count == 0)
        {
            return; // 安全：所有已管理网卡均 Disconnected，避免主动断网。
        }

        var target = available[0];
        var targetAdapter = presentByGuid[target.Guid];

        // 目标网卡被禁用则启用（回切 / 递补）。
        if (targetAdapter.State == AdapterState.Disabled && !InCooldown(target.Guid, now))
        {
            _repository.Enable(targetAdapter);
            EnterCooldown(target.Guid, now);
            _logger?.Info($"自动启用网卡 {targetAdapter.Name}（优先级 {target.Priority}）");
            if (config.NotificationsEnabled)
            {
                Notify($"已切换到 {targetAdapter.Name}");
            }
        }

        // 禁用优先级更低且当前 Up 的网卡；Disconnected 的不动作。
        foreach (var lower in candidates.Where(k => k.Priority > target.Priority))
        {
            var adapter = presentByGuid[lower.Guid];
            if (adapter.State == AdapterState.Up && !InCooldown(lower.Guid, now))
            {
                _repository.Disable(adapter);
                EnterCooldown(lower.Guid, now);
                _logger?.Info($"自动禁用网卡 {adapter.Name}（优先级 {lower.Priority}）");
                if (config.NotificationsEnabled)
                {
                    Notify($"已禁用 {adapter.Name}");
                }
            }
        }
    }

    private void UpdateKnownAdapters(AppConfig config, IReadOnlyList<Adapter> present, DateTime now)
    {
        var byGuid = config.KnownAdapters.ToDictionary(k => k.Guid, StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in present)
        {
            if (byGuid.TryGetValue(adapter.Guid, out var known))
            {
                known.Name = adapter.Name;
                known.Description = adapter.InterfaceDescription;
                known.InterfaceType = adapter.InterfaceType;
                known.LastSeenAt = now;
            }
            else
            {
                config.KnownAdapters.Add(new KnownAdapter
                {
                    Guid = adapter.Guid,
                    Name = adapter.Name,
                    Description = adapter.InterfaceDescription,
                    InterfaceType = adapter.InterfaceType,
                    Priority = NextPriority(config),
                    IsIgnored = adapter.InterfaceType == InterfaceType.Virtual,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }
        }
    }

    private static int NextPriority(AppConfig config)
        => config.KnownAdapters.Count == 0
            ? 1
            : config.KnownAdapters.Max(k => k.Priority) + 1;

    private bool InCooldown(string guid, DateTime now)
        => _cooldownUntil.TryGetValue(guid, out var until) && now < until;

    private void EnterCooldown(string guid, DateTime now)
        => _cooldownUntil[guid] = now + CooldownDuration;

    private void Notify(string message)
    {
        if (_notifier is null)
        {
            return;
        }

        _notifier.Notify("NetSwitch", message);
    }
}
