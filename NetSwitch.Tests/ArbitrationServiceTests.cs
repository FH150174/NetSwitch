using NetSwitch.Core.Arbitration;
using NetSwitch.Core.Models;
using NetSwitch.Infrastructure.Cim;

namespace NetSwitch.Tests;

public sealed class ArbitrationServiceTests
{
    private static DateTime Now => new(2026, 9, 11, 12, 0, 0);

    private static ArbitrationService Build(
        FakeAdapterRepository repo,
        InMemoryConfigStore store,
        FakeNotifier? notifier = null,
        Func<DateTime>? nowProvider = null)
        => new(repo, store, notifier: notifier, nowProvider: nowProvider ?? (() => Now));

    // ───────────────────────── V1.0 既有用例（保持） ─────────────────────────

    [Fact]
    public void 高优先级网卡重新可用时_启用它并禁用低优先级在线网卡()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterLinkState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var service = Build(repo, store);

        // Act
        service.Tick();

        // Assert
        Assert.Equal(new[] { "A" }, repo.EnabledLog);
        Assert.Equal(new[] { "B" }, repo.DisabledLog);
    }

    [Fact]
    public void 高优先级网卡拔出时_启用次高优先级网卡()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("B", "次高", AdapterLinkState.Disabled));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var service = Build(repo, store);

        // Act
        service.Tick();

        // Assert
        Assert.Equal(new[] { "B" }, repo.EnabledLog);
        Assert.Empty(repo.DisabledLog);
    }

    [Fact]
    public void 忽略列表网卡永不被触碰()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "被忽略", AdapterLinkState.Up));
        repo.Add(AdapterFactory.Adapter("B", "目标", AdapterLinkState.Disabled));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1, isIgnored: true), AdapterFactory.Known("B", 2) },
            },
        };

        var service = Build(repo, store);

        // Act
        service.Tick();

        // Assert：B 作为可用目标被启用，A 未被禁用（忽略列表不受影响）。
        Assert.Equal(new[] { "B" }, repo.EnabledLog);
        Assert.DoesNotContain("A", repo.DisabledLog);
        Assert.Equal(AdapterState.Up, repo.Adapters["A"].State);
    }

    [Fact]
    public void 无任何已知网卡存在时_不执行任何操作()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("X", "未知", AdapterLinkState.Up));

        var store = new InMemoryConfigStore();

        var service = Build(repo, store);

        // Act
        service.Tick();

        // Assert
        Assert.Empty(repo.EnabledLog);
        Assert.Empty(repo.DisabledLog);
    }

    [Fact]
    public void 自动模式关闭时_不执行启停()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterLinkState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                AutoArbitrate = false,
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var service = Build(repo, store);

        // Act
        service.Tick();

        // Assert
        Assert.Empty(repo.EnabledLog);
        Assert.Empty(repo.DisabledLog);
    }

    [Fact]
    public void 防抖冷却期内_不重复操作同一网卡()
    {
        // Arrange：Enable 不立即生效（模拟操作延迟），且时钟固定。
        var repo = new FakeAdapterRepository { ApplyImmediately = false };
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterLinkState.Disabled));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1) },
            },
        };

        var service = Build(repo, store);

        // Act
        service.Tick();
        service.Tick();

        // Assert：冷却期内第二次 Tick 不应重复 Enable。
        Assert.Single(repo.EnabledLog);
    }

    [Fact]
    public void 首次发现网卡时_自动入库并记录时间()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("X", "新网卡", AdapterLinkState.Up));

        var store = new InMemoryConfigStore();
        var now = Now;
        var service = Build(repo, store, nowProvider: () => now);

        // Act
        service.Tick();

        // Assert
        var known = Assert.Single(store.Config.KnownAdapters);
        Assert.Equal("X", known.Guid);
        Assert.Equal(now, known.FirstSeenAt);
        Assert.Equal(now, known.LastSeenAt);
    }

    [Fact]
    public void 默认忽略虚拟网卡()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("V", "虚拟网卡", AdapterLinkState.Up, InterfaceType.Virtual));

        var store = new InMemoryConfigStore();
        var service = Build(repo, store);

        // Act
        service.Tick();

        // Assert
        var known = Assert.Single(store.Config.KnownAdapters);
        Assert.True(known.IsIgnored);
    }

    [Fact]
    public void 所有候选均断开时_不执行任何操作()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterLinkState.Disconnected));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterLinkState.Disconnected));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var service = Build(repo, store);

        // Act
        service.Tick();

        // Assert
        Assert.Empty(repo.EnabledLog);
        Assert.Empty(repo.DisabledLog);
    }

    [Fact]
    public void 手动切换时_启用目标并禁用其它已管理在线网卡()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "目标", AdapterLinkState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "其它", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var service = Build(repo, store);

        // Act
        service.SwitchTo("A");

        // Assert
        Assert.Equal(new[] { "A" }, repo.EnabledLog);
        Assert.Equal(new[] { "B" }, repo.DisabledLog);
    }

    [Fact]
    public void 手动切换到断开的目标时_不执行任何操作()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "目标", AdapterLinkState.Disconnected));
        repo.Add(AdapterFactory.Adapter("B", "在线", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var service = Build(repo, store);

        // Act
        service.SwitchTo("A");

        // Assert：目标不可用，不启用也不禁用任何网卡。
        Assert.Empty(repo.EnabledLog);
        Assert.Empty(repo.DisabledLog);
    }

    [Fact]
    public void 手动切换时_不触碰忽略列表与未知网卡()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "目标", AdapterLinkState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "被忽略", AdapterLinkState.Up));
        repo.Add(AdapterFactory.Adapter("C", "未知网卡", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2, isIgnored: true) },
            },
        };

        var service = Build(repo, store);

        // Act
        service.SwitchTo("A");

        // Assert：启用目标；忽略网卡与未知网卡均不被禁用。
        Assert.Equal(new[] { "A" }, repo.EnabledLog);
        Assert.DoesNotContain("B", repo.DisabledLog);
        Assert.DoesNotContain("C", repo.DisabledLog);
        Assert.Equal(AdapterState.Up, repo.Adapters["B"].State);
        Assert.Equal(AdapterState.Up, repo.Adapters["C"].State);
    }

    [Fact]
    public void 手动切换不受冷却期影响_立即启用目标()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "目标A", AdapterLinkState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "目标B", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var now = Now;
        var service = Build(repo, store, nowProvider: () => now);

        // 第一次切换：启用 A、禁用 B，两者都进入 3 秒冷却。
        service.SwitchTo("A");
        Assert.Equal(new[] { "A" }, repo.EnabledLog);
        Assert.Equal(new[] { "B" }, repo.DisabledLog);

        // 立即切回 B（B 仍在冷却期）：应绕过冷却立即生效，而非被忽略。
        service.SwitchTo("B");

        // Assert
        Assert.Equal(new[] { "A", "B" }, repo.EnabledLog);
        Assert.Equal(new[] { "B", "A" }, repo.DisabledLog);
    }

    [Fact]
    public void 刚出现的网卡在观察期内不触发切换_稳定超过观察期后才切换()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Absent("A", "高优先"));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var now = Now;
        var service = Build(repo, store, nowProvider: () => now);

        // 基线：A 不存在、B 在线，无操作。
        service.Tick();
        Assert.Empty(repo.DisabledLog);

        // A 短暂出现（WMI 抖动）。
        repo.SetLinkState("A", AdapterLinkState.Up);
        now = now.AddSeconds(1);

        // 观察期内：A 刚出现，不应因 A 出现而禁用 B。
        service.Tick();
        Assert.Empty(repo.DisabledLog);

        // A 稳定存在超过观察期后，切换回 A 并禁用 B。
        now = now.AddSeconds(9);
        service.Tick();
        Assert.Equal(new[] { "B" }, repo.DisabledLog);
    }

    [Fact]
    public void 手动切换后自动仲裁不因观察期而撤销()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterLinkState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var now = Now;
        var service = Build(repo, store, nowProvider: () => now);

        // 手动切换到 A：启用 A、禁用 B。
        service.SwitchTo("A");
        Assert.Equal(new[] { "A" }, repo.EnabledLog);
        Assert.Equal(new[] { "B" }, repo.DisabledLog);

        // 自动仲裁 tick（观察期内）：不应因 A「刚出现」而重新启用 B。
        now = now.AddSeconds(1);
        service.Tick();

        // Assert：B 未被重新启用。
        Assert.Equal(new[] { "A" }, repo.EnabledLog);
    }

    // ───────────────────────── V1.1 新增用例（规格 §12 T1~T8） ─────────────────────────

    /// <summary>
    /// T1：优先级 1 的网卡**设备存在但链路未连接**（正是 D1 现场：RNDIS 设备已插上、
    /// 但链路状态未就绪）时，不得判为「不存在」，也不得执行任何启停。
    /// </summary>
    [Fact]
    public void T1_优先级1网卡存在但未连接时_不做任何启停且不判为不存在()
    {
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "以太网 3", AdapterLinkState.Disconnected));
        repo.Add(AdapterFactory.Adapter("B", "以太网", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        Build(repo, store).Tick();

        Assert.Empty(repo.EnabledLog);
        Assert.Empty(repo.DisabledLog);
        Assert.Equal(AdapterPresence.Present, repo.Adapters["A"].Presence);
        Assert.Equal(AdapterState.Disconnected, repo.Adapters["A"].State);
        Assert.NotEqual(AdapterState.NotPresent, repo.Adapters["A"].State);
    }

    /// <summary>T2：优先级 1 的网卡设备确实不存在时，不做任何启停。</summary>
    [Fact]
    public void T2_优先级1网卡确实不存在时_不做任何启停()
    {
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Absent("A", "已拔出的网卡"));
        repo.Add(AdapterFactory.Adapter("B", "以太网", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        Build(repo, store).Tick();

        Assert.Empty(repo.EnabledLog);
        Assert.Empty(repo.DisabledLog);
        Assert.Equal(AdapterState.NotPresent, repo.Adapters["A"].State);
    }

    /// <summary>T3：链路状态未知时安全优先——不做任何启停，也不产生「已禁用」副作用。</summary>
    [Fact]
    public void T3_链路状态未知时_不做任何启停()
    {
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "状态未知", AdapterLinkState.Unknown));
        repo.Add(AdapterFactory.Adapter("B", "以太网", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        Build(repo, store).Tick();

        Assert.Empty(repo.EnabledLog);
        Assert.Empty(repo.DisabledLog);
        Assert.Equal(AdapterState.Up, repo.Adapters["B"].State);
    }

    /// <summary>T4：<c>admin == 2 &amp;&amp; oper == 6</c> 必须映射为「已禁用」，绝不是「不存在」。</summary>
    [Fact]
    public void T4_管理员禁用且oper为6时_映射为已禁用而非不存在()
    {
        var (presence, link) = AdapterStateMapper.MapActive(
            operationalStatus: 6, adminStatus: 2, mediaConnectState: 1, pnpDevicePresent: true);

        Assert.Equal(AdapterPresence.Present, presence);
        Assert.Equal(AdapterLinkState.Disabled, link);
    }

    /// <summary>
    /// T5：<c>oper == 6</c> 但设备节点存在（RNDIS/USB 驱动在链路未就绪时会上报 6）
    /// 必须映射为「存在」，绝不是「不存在」。
    /// </summary>
    [Fact]
    public void T5_oper为6但设备节点存在时_绝不判为不存在()
    {
        var (presence, link) = AdapterStateMapper.MapActive(
            operationalStatus: 6, adminStatus: 1, mediaConnectState: 1, pnpDevicePresent: true);

        Assert.Equal(AdapterPresence.Present, presence);
        Assert.NotEqual(AdapterPresence.Absent, presence);
        Assert.True(link is AdapterLinkState.Up or AdapterLinkState.Disconnected,
            $"期望 Up 或 Disconnected，实际 {link}");
    }

    /// <summary>T5b：<c>oper == 6</c> 且设备节点确实不存在时才判为「不存在」。</summary>
    [Fact]
    public void T5b_oper为6且设备节点不存在时_判为不存在()
    {
        var (presence, link) = AdapterStateMapper.MapActive(
            operationalStatus: 6, adminStatus: 1, mediaConnectState: 0, pnpDevicePresent: false);

        Assert.Equal(AdapterPresence.Absent, presence);
        Assert.Equal(AdapterLinkState.Unknown, link);
    }

    /// <summary>T6：快照含重复 GUID 时不得抛异常，且只按首条生效。</summary>
    [Fact]
    public void T6_快照含重复GUID时_不抛异常且按首条生效()
    {
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "以太网 3", AdapterLinkState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "以太网", AdapterLinkState.Up));

        // 同一 GUID 再塞一条（模拟仓库去重失效）：后一条为 Up，若被采用则 A 不会被启用。
        repo.RawExtras.Add(AdapterFactory.Adapter("A", "以太网 3 重复条目", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var service = Build(repo, store);
        var exception = Record.Exception(() => service.Tick());

        Assert.Null(exception);
        Assert.Equal(new[] { "A" }, repo.EnabledLog);
        Assert.Equal(new[] { "B" }, repo.DisabledLog);
    }

    /// <summary>
    /// T7：观察期语义——观察期内不成为 target；结束后成为 target；
    /// **离开可用状态后时间戳必须被清除**，否则下次可用时会带着旧时间戳被误判为已过观察期。
    /// </summary>
    [Fact]
    public void T7_离开可用状态后观察期时间戳被清除()
    {
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Absent("A", "高优先"));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var now = Now;
        var service = Build(repo, store, nowProvider: () => now);

        // 基线：A 不存在。
        service.Tick();
        Assert.Empty(repo.DisabledLog);

        // A 出现并可用 → 进入观察期。
        repo.SetLinkState("A", AdapterLinkState.Up);
        now = now.AddSeconds(1);
        service.Tick();
        Assert.Empty(repo.DisabledLog);
        Assert.True(service.IsInGracePeriod("A"));

        // A 又离开可用状态 → 时间戳应被清除。
        repo.SetLinkState("A", AdapterLinkState.Disconnected);
        now = now.AddSeconds(1);
        service.Tick();
        Assert.False(service.IsInGracePeriod("A"));

        // A 再次可用；此时距「第一次出现」已超过 8 秒。
        // 若时间戳未被清除，这里会误判为已过观察期而禁用 B。
        repo.SetLinkState("A", AdapterLinkState.Up);
        now = now.AddSeconds(9);
        service.Tick();
        Assert.Empty(repo.DisabledLog);

        // 从「这次出现」起稳定超过观察期后，A 才成为 target 并禁用 B。
        now = now.AddSeconds(9);
        service.Tick();
        Assert.Equal(new[] { "B" }, repo.DisabledLog);
    }

    /// <summary>T8：存在性判定必须忽略大小写——实测同一设备的 PnpDeviceID 大小写不一致。</summary>
    [Fact]
    public void T8_PnP存在性判定忽略大小写()
    {
        var set = new PnpPresenceSet(new[] { @"USB\VID_1A2B&PID_3C4D\8&324BD37&0&0000" });

        Assert.True(set.Contains(@"USB\VID_1A2B&PID_3C4D\8&324bd37&0&0000"));
        Assert.True(set.Contains(@"usb\vid_1a2b&pid_3c4d\8&324bd37&0&0000"));
        Assert.False(set.Contains(@"USB\VID_1A2B&PID_3C4D\8&00000000&0&0000"));
        Assert.False(set.Contains(null));
        Assert.False(set.Contains("   "));
    }

    /// <summary>S8：通知失败绝不应中断仲裁周期（历史上曾因通知异常逃逸导致整个 Tick 失败）。</summary>
    [Fact]
    public void S8_通知失败不中断仲裁周期()
    {
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterLinkState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterLinkState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var notifier = new FakeNotifier { ThrowOnNotify = true };
        var service = Build(repo, store, notifier);

        var exception = Record.Exception(() => service.Tick());

        Assert.Null(exception);
        Assert.Equal(new[] { "A" }, repo.EnabledLog);
        Assert.Equal(new[] { "B" }, repo.DisabledLog);
    }
}
