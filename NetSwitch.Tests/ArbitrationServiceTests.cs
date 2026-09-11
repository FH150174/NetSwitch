using NetSwitch.Core.Arbitration;
using NetSwitch.Core.Models;

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

    [Fact]
    public void 高优先级网卡重新可用时_启用它并禁用低优先级在线网卡()
    {
        // Arrange
        var repo = new FakeAdapterRepository();
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterState.Up));

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
        repo.Add(AdapterFactory.Adapter("B", "次高", AdapterState.Disabled));

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
        repo.Add(AdapterFactory.Adapter("A", "被忽略", AdapterState.Up));
        repo.Add(AdapterFactory.Adapter("B", "目标", AdapterState.Disabled));

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
        repo.Add(AdapterFactory.Adapter("X", "未知", AdapterState.Up));

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
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterState.Up));

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
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterState.Disabled));

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
        repo.Add(AdapterFactory.Adapter("X", "新网卡", AdapterState.Up));

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
        repo.Add(AdapterFactory.Adapter("V", "虚拟网卡", AdapterState.Up, InterfaceType.Virtual));

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
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterState.Disconnected));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterState.Disconnected));

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
        repo.Add(AdapterFactory.Adapter("A", "目标", AdapterState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "其它", AdapterState.Up));

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
        repo.Add(AdapterFactory.Adapter("A", "目标", AdapterState.Disconnected));
        repo.Add(AdapterFactory.Adapter("B", "在线", AdapterState.Up));

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
        repo.Add(AdapterFactory.Adapter("A", "目标", AdapterState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "被忽略", AdapterState.Up));
        repo.Add(AdapterFactory.Adapter("C", "未知网卡", AdapterState.Up));

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
        repo.Add(AdapterFactory.Adapter("A", "目标A", AdapterState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "目标B", AdapterState.Up));

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
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterState.NotPresent));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterState.Up));

        var store = new InMemoryConfigStore
        {
            Config = new AppConfig
            {
                KnownAdapters = { AdapterFactory.Known("A", 1), AdapterFactory.Known("B", 2) },
            },
        };

        var now = Now;
        var service = Build(repo, store, nowProvider: () => now);

        // 基线：A 消失、B 在线，无操作。
        service.Tick();
        Assert.Empty(repo.DisabledLog);

        // A 短暂出现（WMI 抖动）。
        repo.SetState("A", AdapterState.Up);
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
        repo.Add(AdapterFactory.Adapter("A", "高优先", AdapterState.Disabled));
        repo.Add(AdapterFactory.Adapter("B", "低优先", AdapterState.Up));

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
}
