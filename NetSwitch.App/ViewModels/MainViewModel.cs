using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetSwitch.App.Services;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Arbitration;
using NetSwitch.Core.Models;

// SyncSettingsFromConfig / OnAutoStartEnabledChanged 回滚等场景需直接读写后备字段，
// 以避免触发 [ObservableProperty] 生成的 setter 造成保存副作用或递归。
#pragma warning disable MVVMTK0034

namespace NetSwitch.App.ViewModels;

/// <summary>
/// 主窗口视图模型：网卡列表、优先级排序、自动模式、手动切换、自启与静默启动开关。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IAdapterRepository _repository;
    private readonly IConfigStore _configStore;
    private readonly ArbitrationService _arbitration;
    private readonly AutoStartService _autoStart;
    private readonly ILogger _logger;
    private readonly Action? _openLog;
    private readonly Func<bool>? _confirmEnableSilent;

    public ObservableCollection<AdapterItemViewModel> Adapters { get; } = new();

    public int[] PollIntervals { get; } = { 1, 2, 3, 5, 10 };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoArbitrateText))]
    private bool autoArbitrate;

    [ObservableProperty]
    private bool autoStartEnabled;

    /// <summary>静默启动开关（规格 §10.3）：启动后不显示主窗口，直接驻留托盘。</summary>
    [ObservableProperty]
    private bool startSilently;

    [ObservableProperty]
    private bool notificationsEnabled;

    [ObservableProperty]
    private int pollIntervalSeconds;

    [ObservableProperty]
    private AdapterItemViewModel? selectedAdapter;

    [ObservableProperty]
    private string statusText = "就绪";

    public MainViewModel(
        IAdapterRepository repository,
        IConfigStore configStore,
        ArbitrationService arbitration,
        AutoStartService autoStart,
        ILogger logger,
        Action? openLog = null,
        Func<bool>? confirmEnableSilent = null)
    {
        _repository = repository;
        _configStore = configStore;
        _arbitration = arbitration;
        _autoStart = autoStart;
        _logger = logger;
        _openLog = openLog;
        _confirmEnableSilent = confirmEnableSilent;
    }

    public string AutoArbitrateText => AutoArbitrate ? "自动模式：开" : "自动模式：关";

    /// <summary>
    /// 初始化：加载配置并刷新列表。
    /// </summary>
    public void Initialize()
    {
        SyncSettingsFromConfig();

        // 以系统实际计划任务状态校正自启开关，避免 UI 与真实状态长期不一致；
        // 任务失效（exe 被移动 / 参数过期）时自动重建（规格 §10.2）。
        try
        {
            var actual = _autoStart.EnsureUpToDate();
            var config = _configStore.Load();
            if (config.AutoStartEnabled != actual)
            {
                config.AutoStartEnabled = actual;
                _configStore.Save(config);
            }

            autoStartEnabled = actual;
            OnPropertyChanged(nameof(AutoStartEnabled));
        }
        catch (Exception ex)
        {
            _logger.Warn($"校验开机自启状态失败: {ex.Message}");
        }

        Refresh();
    }

    /// <summary>
    /// 从配置同步设置项（用字段赋值避免触发保存副作用），供托盘等外部入口变更后回填 UI。
    /// </summary>
    public void SyncSettingsFromConfig()
    {
        var config = _configStore.Load();
        autoArbitrate = config.AutoArbitrate;
        autoStartEnabled = config.AutoStartEnabled;
        startSilently = config.StartSilently;
        notificationsEnabled = config.NotificationsEnabled;
        pollIntervalSeconds = config.PollIntervalSeconds;
        OnPropertyChanged(nameof(AutoArbitrate));
        OnPropertyChanged(nameof(AutoArbitrateText));
        OnPropertyChanged(nameof(AutoStartEnabled));
        OnPropertyChanged(nameof(StartSilently));
        OnPropertyChanged(nameof(NotificationsEnabled));
        OnPropertyChanged(nameof(PollIntervalSeconds));
    }

    public void Refresh()
    {
        try
        {
            var config = _configStore.Load();

            // D4：容错建表。快照出现重复 GUID 时保留首条并记 WARN，
            // 绝不因为一条异常数据抛异常导致整表停止更新（用户看到的是长期冻结的旧状态）。
            var present = new Dictionary<string, Adapter>(StringComparer.OrdinalIgnoreCase);
            foreach (var adapter in _repository.GetPresentAdapters())
            {
                if (!present.TryAdd(adapter.Guid, adapter))
                {
                    _logger.Warn($"快照出现重复 GUID，已忽略后出现的条目: {adapter.Guid}");
                }
            }

            var byGuid = new Dictionary<string, AdapterItemViewModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var vm in Adapters)
            {
                byGuid.TryAdd(vm.Guid, vm);
            }

            // 已知库条目被清理（例如遗留幽灵条目）后，同步移除对应的列表项。
            var knownGuids = config.KnownAdapters
                .Select(k => k.Guid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = Adapters.Count - 1; i >= 0; i--)
            {
                if (!knownGuids.Contains(Adapters[i].Guid))
                {
                    Adapters.RemoveAt(i);
                }
            }

            foreach (var known in config.KnownAdapters)
            {
                present.TryGetValue(known.Guid, out var adapter);
                var inGrace = adapter is not null && _arbitration.IsInGracePeriod(known.Guid);
                var remaining = inGrace ? _arbitration.GracePeriodRemainingSeconds(known.Guid) : 0;

                if (byGuid.TryGetValue(known.Guid, out var vm))
                {
                    vm.Priority = known.Priority;
                    vm.IsIgnored = known.IsIgnored;
                    vm.UpdateFrom(adapter, inGrace, remaining);
                }
                else
                {
                    var created = new AdapterItemViewModel(known, adapter, _logger, inGrace, remaining);
                    Adapters.Add(created);
                    byGuid[created.Guid] = created;
                }
            }

            SortByPriority();
            StatusText = BuildStatusText(config);
        }
        catch (Exception ex)
        {
            _logger.Error("刷新状态失败", ex);
            StatusText = $"刷新失败：{ex.Message}";
        }
    }

    /// <summary>状态栏文案，含 D2 要求的「观察中」可见性。</summary>
    private string BuildStatusText(AppConfig config)
    {
        var baseline = $"已管理 {config.KnownAdapters.Count} 个网卡";

        var inGrace = Adapters.Where(a => a.IsInGracePeriod).ToList();
        if (inGrace.Count == 0)
        {
            return baseline;
        }

        var names = string.Join("、", inGrace.Select(a => a.Name));
        var seconds = inGrace.Max(a => a.GraceRemainingSeconds);
        return $"{baseline} · 观察中：{names}（约 {seconds} 秒后参与仲裁）";
    }

    [RelayCommand]
    private void MoveUp(AdapterItemViewModel? adapter)
    {
        if (adapter is null || adapter.IsIgnored)
        {
            return;
        }

        var ordered = OrderedCandidates();
        var index = ordered.IndexOf(adapter);
        if (index <= 0)
        {
            return;
        }

        SwapPriority(ordered[index - 1], adapter);
        SaveAndRefresh();
    }

    [RelayCommand]
    private void MoveDown(AdapterItemViewModel? adapter)
    {
        if (adapter is null || adapter.IsIgnored)
        {
            return;
        }

        var ordered = OrderedCandidates();
        var index = ordered.IndexOf(adapter);
        if (index < 0 || index >= ordered.Count - 1)
        {
            return;
        }

        SwapPriority(adapter, ordered[index + 1]);
        SaveAndRefresh();
    }

    [RelayCommand]
    private void ToggleIgnore(AdapterItemViewModel? adapter)
    {
        if (adapter is null)
        {
            return;
        }

        adapter.IsIgnored = !adapter.IsIgnored;
        SaveAndRefresh();
    }

    [RelayCommand]
    private void SwitchTo(AdapterItemViewModel? adapter)
    {
        if (adapter is null || adapter.IsIgnored)
        {
            return;
        }

        try
        {
            _arbitration.SwitchTo(adapter.Guid);
            Refresh();
            StatusText = $"已切换到 {adapter.Name}";
        }
        catch (Exception ex)
        {
            _logger.Error($"手动切换失败: {adapter.Name}", ex);
            StatusText = $"切换失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshNow() => Refresh();

    [RelayCommand]
    private void OpenLog() => _openLog?.Invoke();

    private List<AdapterItemViewModel> OrderedCandidates()
        => Adapters.Where(a => !a.IsIgnored).OrderBy(a => a.Priority).ToList();

    private static void SwapPriority(AdapterItemViewModel a, AdapterItemViewModel b)
        => (a.Priority, b.Priority) = (b.Priority, a.Priority);

    private void SaveAndRefresh()
    {
        var config = _configStore.Load();
        foreach (var vm in Adapters)
        {
            var known = config.KnownAdapters.FirstOrDefault(k =>
                string.Equals(k.Guid, vm.Guid, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                continue;
            }

            known.Priority = vm.Priority;
            known.IsIgnored = vm.IsIgnored;
        }

        _configStore.Save(config);
        Refresh();
    }

    private void SortByPriority()
    {
        var sorted = Adapters
            .OrderBy(a => a.IsIgnored ? 1 : 0)
            .ThenBy(a => a.Priority)
            .ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Adapters[i];
            var target = sorted[i];
            if (current != target)
            {
                Adapters.Move(Adapters.IndexOf(target), i);
            }
        }
    }

    partial void OnAutoArbitrateChanged(bool value)
    {
        var config = _configStore.Load();
        config.AutoArbitrate = value;
        _configStore.Save(config);
    }

    partial void OnAutoStartEnabledChanged(bool value)
    {
        try
        {
            if (value)
            {
                _autoStart.Enable();

                // 规格 §10.3：首次开启自启时若未勾选静默启动，提示并建议勾选。
                if (!StartSilently && (_confirmEnableSilent?.Invoke() ?? false))
                {
                    StartSilently = true;
                }
            }
            else
            {
                _autoStart.Disable();
            }

            var config = _configStore.Load();
            config.AutoStartEnabled = value;
            _configStore.Save(config);
        }
        catch (Exception ex)
        {
            _logger.Error("切换开机自启失败", ex);
            StatusText = $"开机自启设置失败：{ex.Message}";
            autoStartEnabled = !value;
            OnPropertyChanged(nameof(AutoStartEnabled));
        }
    }

    partial void OnStartSilentlyChanged(bool value)
    {
        var config = _configStore.Load();
        config.StartSilently = value;
        _configStore.Save(config);
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        var config = _configStore.Load();
        config.NotificationsEnabled = value;
        _configStore.Save(config);
    }

    partial void OnPollIntervalSecondsChanged(int value)
    {
        var config = _configStore.Load();
        config.PollIntervalSeconds = Math.Max(1, value);
        _configStore.Save(config);
    }
}
