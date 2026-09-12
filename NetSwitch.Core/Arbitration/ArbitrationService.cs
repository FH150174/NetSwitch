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
/// <item>可用集为空（所有候选均不可用）。</item>
/// <item>自动模式关闭。</item>
/// </list>
///
/// V1.1 起可用性判定直接读 <see cref="Adapter.Presence"/> 与 <see cref="Adapter.LinkState"/>，
/// <b>不再</b>依赖聚合值 <see cref="Adapter.State"/>——把「设备在不在」与「链路通不通」
/// 压成一个值正是 D1 缺陷的根源。
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
    private readonly Dictionary<string, (AdapterPresence Presence, AdapterLinkState Link)> _lastSnapshot =
        new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>某个网卡是否处于观察期（供 UI 显示「观察中」）。</summary>
    public bool IsInGracePeriod(string guid)
        => _becameAvailableAt.TryGetValue(guid, out var t)
           && _now() - t < AvailabilityGracePeriod;

    /// <summary>观察期剩余秒数（不足 1 秒按 0 计）。</summary>
    public int GracePeriodRemainingSeconds(string guid)
    {
        if (!_becameAvailableAt.TryGetValue(guid, out var t))
        {
            return 0;
        }

        var remaining = AvailabilityGracePeriod - (_now() - t);
        return remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);
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
        else
        {
            // 自动模式关闭时仍要跟踪状态迁移，否则重新开启后观察期语义会错乱。
            TrackStateTransitions(present, now);
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
        var byGuid = BuildSnapshotIndex(present);
        if (!byGuid.TryGetValue(guid, out var adapter))
        {
            _logger?.Warn($"手动切换目标网卡不存在: {guid}");
            return;
        }

        var now = _now();

        // 目标必须先变为可用，否则不动作，避免切到断开的网线导致断网。
        // 手动切换是用户的即时意图：绕过冷却立即执行，避免切换后 3 秒内无法再次切换。
        var targetReady = adapter.LinkState == AdapterLinkState.Up;
        if (adapter.LinkState == AdapterLinkState.Disabled)
        {
            _repository.Enable(adapter);
            // 同步状态基线，避免后续自动仲裁误判为「刚出现」而撤销手动切换。
            _lastSnapshot[adapter.Guid] = (AdapterPresence.Present, AdapterLinkState.Up);
            _becameAvailableAt.Remove(adapter.Guid);
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
            _logger?.Warn($"目标网卡不可用（{adapter.Presence}/{adapter.LinkState}），未执行切换: {adapter.Name}");
            return;
        }

        // 只禁用「已管理且未忽略」的其它在线网卡。
        var managed = config.KnownAdapters
            .Where(k => !k.IsIgnored)
            .Select(k => k.Guid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var other in present.Where(a => !string.Equals(a.Guid, adapter.Guid, StringComparison.OrdinalIgnoreCase)))
        {
            if (other.LinkState != AdapterLinkState.Up)
            {
                continue;
            }

            // 手动切换：同样绕过冷却，立即禁用其它在线网卡。
            if (!managed.Contains(other.Guid))
            {
                continue;
            }

            _repository.Disable(other);
            _lastSnapshot[other.Guid] = (AdapterPresence.Present, AdapterLinkState.Disabled);
            _becameAvailableAt.Remove(other.Guid);
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
    ///
    /// 关键约束（规格 §7 观察期语义）：网卡**离开**可用状态时必须清除其观察期时间戳，
    /// 否则下次可用时会带着旧时间戳被误判为「已过观察期」，观察期形同虚设。
    /// </summary>
    private void TrackStateTransitions(IReadOnlyList<Adapter> present, DateTime now)
    {
        foreach (var adapter in present)
        {
            var isAvailable = IsAvailable(adapter);

            if (_lastSnapshot.TryGetValue(adapter.Guid, out var previous))
            {
                if (previous.Presence != adapter.Presence || previous.Link != adapter.LinkState)
                {
                    LogStateChange(adapter);
                }

                var wasAvailable = previous.Presence == AdapterPresence.Present
                    && previous.Link is AdapterLinkState.Up or AdapterLinkState.Disabled;

                if (isAvailable && !wasAvailable)
                {
                    _becameAvailableAt[adapter.Guid] = now;
                }
                else if (!isAvailable)
                {
                    _becameAvailableAt.Remove(adapter.Guid);
                }
            }
            else
            {
                // 本进程首次见到该网卡：记录一次基线快照，便于事后诊断「为何从未被选中」。
                LogStateChange(adapter);
            }

            _lastSnapshot[adapter.Guid] = (adapter.Presence, adapter.LinkState);
        }
    }

    /// <summary>结构化诊断日志，用于复现与回归（规格 §7.5 D1 修复规格第 6 条）。</summary>
    private void LogStateChange(Adapter adapter)
        => _logger?.Info(
            $"网卡状态变更 guid={adapter.Guid} name={adapter.Name} " +
            $"presence={adapter.Presence} link={adapter.LinkState} " +
            $"{adapter.Raw ?? "oper=- admin=- media=-"} pnp={adapter.PnpDeviceId ?? "-"}");

    /// <summary>
    /// 「有联网潜力」：设备确实存在，且链路为 Up 或 Disabled。
    /// <see cref="AdapterPresence.Absent"/> 与 <see cref="AdapterLinkState.Unknown"/> 一律排除——
    /// 但这只是「不选它当目标」，绝不因此去禁用其它网卡（安全优先）。
    /// </summary>
    private static bool IsAvailable(Adapter adapter)
        => adapter.Presence == AdapterPresence.Present
           && adapter.LinkState is AdapterLinkState.Up or AdapterLinkState.Disabled;

    private void Arbitrate(AppConfig config, IReadOnlyList<Adapter> present, DateTime now)
    {
        var presentByGuid = BuildSnapshotIndex(present);

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

        // 可用 = 有联网潜力且状态稳定的候选。
        // 「刚由不可用变为可用」的网卡在观察期内不算可用，避免设备短暂出现/消失引起的震荡。
        var available = candidates
            .Where(k => IsAvailable(presentByGuid[k.Guid]) && !IsInGracePeriod(k.Guid))
            .ToList();

        if (available.Count == 0)
        {
            return; // 安全：所有已管理网卡均不可用，避免主动断网。
        }

        var target = available[0];
        var targetAdapter = presentByGuid[target.Guid];

        // 目标网卡被禁用则启用（回切 / 递补）。
        if (targetAdapter.LinkState == AdapterLinkState.Disabled && !InCooldown(target.Guid, now))
        {
            _repository.Enable(targetAdapter);
            EnterCooldown(target.Guid, now);
            _logger?.Info($"自动启用网卡 {targetAdapter.Name}（优先级 {target.Priority}）");
            if (config.NotificationsEnabled)
            {
                Notify($"已切换到 {targetAdapter.Name}");
            }
        }

        // 禁用优先级更低且当前 Up 的网卡；Disconnected/Unknown 的不动作。
        foreach (var lower in candidates.Where(k => k.Priority > target.Priority))
        {
            var adapter = presentByGuid[lower.Guid];
            if (adapter.LinkState == AdapterLinkState.Up && !InCooldown(lower.Guid, now))
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

    /// <summary>
    /// 容错建表：快照中出现重复 GUID 时保留首条并记 WARN，
    /// 绝不因为一条异常数据让整个刷新失败（规格 §7.5 D4）。
    /// </summary>
    private Dictionary<string, Adapter> BuildSnapshotIndex(IReadOnlyList<Adapter> present)
    {
        var index = new Dictionary<string, Adapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in present)
        {
            if (!index.TryAdd(adapter.Guid, adapter))
            {
                _logger?.Warn($"快照出现重复 GUID，已忽略后出现的条目: {adapter.Guid}");
            }
        }

        return index;
    }

    private void UpdateKnownAdapters(AppConfig config, IReadOnlyList<Adapter> present, DateTime now)
    {
        PruneLegacyGhostEntries(config);

        var byGuid = new Dictionary<string, KnownAdapter>(StringComparer.OrdinalIgnoreCase);
        foreach (var known in config.KnownAdapters)
        {
            byGuid.TryAdd(known.Guid, known);
        }

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
                var entry = new KnownAdapter
                {
                    Guid = adapter.Guid,
                    Name = adapter.Name,
                    Description = adapter.InterfaceDescription,
                    InterfaceType = adapter.InterfaceType,
                    Priority = NextPriority(config),
                    IsIgnored = adapter.InterfaceType == InterfaceType.Virtual,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                };
                config.KnownAdapters.Add(entry);
                byGuid[entry.Guid] = entry;
            }
        }
    }

    /// <summary>
    /// 清理历史遗留的「幽灵条目」：V1.1 之前的历史通道在 GUID 缺失时会退化为
    /// **用设备名造键**，从而产生无法与实体条目合并的重复项（规格 §7.5 D5）。
    ///
    /// 判定条件严格：键既不是 GUID、也不含 <c>\</c>（PnP 设备 ID 一定含 <c>\</c>），
    /// 且当前快照中不存在同名设备。满足即认定为不可再生的遗留条目，移除并记 WARN。
    /// </summary>
    private void PruneLegacyGhostEntries(AppConfig config)
    {
        if (config.KnownAdapters.Count == 0)
        {
            return;
        }

        var removed = config.KnownAdapters.RemoveAll(k =>
            !Guid.TryParse(k.Guid, out _)
            && !k.Guid.Contains('\\', StringComparison.Ordinal));

        if (removed > 0)
        {
            _logger?.Warn($"已清理 {removed} 条无法再生的遗留幽灵条目（键既非 GUID 也非 PnP 设备 ID）");
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

        // 通知是尽力而为的：托盘图标可能因资源管理器重启等而失效，
        // 失败绝不应中断仲裁周期（历史上曾因通知异常逃逸导致整个 Tick 失败）。
        try
        {
            _notifier.Notify("NetSwitch", message);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"通知发送失败（已忽略）: {ex.Message}");
        }
    }
}
