using CommunityToolkit.Mvvm.ComponentModel;
using NetSwitch.Core.Abstractions;
using NetSwitch.Core.Models;

namespace NetSwitch.App.ViewModels;

/// <summary>
/// 单个网卡在列表中的展示模型。
///
/// V1.1 修正（规格 §7.5 D3）：<see cref="Name"/> / <see cref="Description"/> /
/// <see cref="InterfaceType"/> 改为可观察属性，并在 <see cref="UpdateFrom"/> 中同步——
/// 原实现是只读属性，构造后永不刷新，网卡改名/换描述后 UI 会长期显示旧值。
/// </summary>
public partial class AdapterItemViewModel : ObservableObject
{
    private readonly ILogger? _logger;

    public string Guid { get; }

    [ObservableProperty]
    private string name;

    [ObservableProperty]
    private string description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InterfaceTypeText))]
    private InterfaceType interfaceType;

    /// <summary>设备是否存在（与链路状态正交）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private AdapterPresence presence;

    /// <summary>链路可用性。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private AdapterLinkState linkState;

    /// <summary>展示层聚合状态（供 XAML 状态点着色）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private AdapterState state;

    /// <summary>是否处于「刚由不可用变为可用」的观察期（D2：让「已连接却不切换」可见）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool isInGracePeriod;

    /// <summary>观察期剩余秒数。</summary>
    [ObservableProperty]
    private int graceRemainingSeconds;

    [ObservableProperty]
    private int priority;

    [ObservableProperty]
    private bool isIgnored;

    public AdapterItemViewModel(
        KnownAdapter known,
        Adapter? adapter,
        ILogger? logger = null,
        bool isInGracePeriod = false,
        int graceRemainingSeconds = 0)
    {
        _logger = logger;
        Guid = known.Guid;
        name = known.Name;
        description = known.Description;
        interfaceType = known.InterfaceType;
        priority = known.Priority;
        isIgnored = known.IsIgnored;
        this.isInGracePeriod = isInGracePeriod;
        this.graceRemainingSeconds = graceRemainingSeconds;

        Apply(adapter);
    }

    /// <summary>
    /// 状态文案。规格 §7.5 D1 修复规格第 4 条：
    /// 「不存在」**仅在** <see cref="AdapterPresence.Absent"/> 时使用。
    /// </summary>
    public string StateText
    {
        get
        {
            var text = Presence == AdapterPresence.Absent
                ? "不存在"
                : LinkState switch
                {
                    AdapterLinkState.Up => "已连接",
                    AdapterLinkState.Disconnected => "已断开",
                    AdapterLinkState.Disabled => "已禁用",
                    _ => "状态未知",
                };

            return IsInGracePeriod ? $"{text} · 观察中" : text;
        }
    }

    public string InterfaceTypeText => InterfaceType switch
    {
        InterfaceType.Ethernet => "以太网",
        InterfaceType.WiFi => "WiFi",
        InterfaceType.Virtual => "虚拟",
        _ => "其他",
    };

    /// <summary>
    /// 用最新快照刷新本项。观察期只影响「能否成为仲裁目标」，
    /// 不影响状态显示与存在性（规格 §7 观察期语义）。
    /// </summary>
    public void UpdateFrom(Adapter? adapter, bool isInGracePeriod, int graceRemainingSeconds)
    {
        IsInGracePeriod = isInGracePeriod;
        GraceRemainingSeconds = graceRemainingSeconds;
        Apply(adapter);
    }

    private void Apply(Adapter? adapter)
    {
        if (adapter is null)
        {
            Presence = AdapterPresence.Absent;
            LinkState = AdapterLinkState.Unknown;
            State = AdapterState.NotPresent;
            return;
        }

        // D3：名称/描述/类型变化时同步刷新；名称变化额外记一条 INFO 日志。
        if (!string.Equals(Name, adapter.Name, StringComparison.Ordinal))
        {
            _logger?.Info($"网卡名称变更 guid={Guid} 「{Name}」-> 「{adapter.Name}」");
            Name = adapter.Name;
        }

        if (!string.Equals(Description, adapter.InterfaceDescription, StringComparison.Ordinal))
        {
            Description = adapter.InterfaceDescription;
        }

        InterfaceType = adapter.InterfaceType;
        Presence = adapter.Presence;
        LinkState = adapter.LinkState;
        State = adapter.State;
    }
}
